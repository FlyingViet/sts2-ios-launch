#!/usr/bin/env python3
"""Precompile Godot's Metal shader cache into .metallib binaries ("baking"), and list the pipelines to warm up.

Godot 4.5 on iOS compiles each shader's Metal source the first time it's drawn, on the render thread. That's
~0.3 s per new effect whenever iOS's own Metal cache is empty: after installing, after an iOS update, or after
iOS clears app caches. Godot can also load shaders that already contain a compiled .metallib (its export-time
"shader baker" format), which skips that compile.

This takes shader_cache folders harvested from a device (tools/harvest_shaders.sh), compiles every Metal source
in them with Xcode's Metal compiler using the same options Godot uses at runtime, and writes the same cache
files with the compiled library added. build.sh puts them in the game data at res://.godot/shader_cache, where
Godot looks after its own cache in Documents (src/ios/shader_cache_prep.cpp removes the uncompiled copies).

Compiled libraries still leave the last step to the device: building each pipeline for its GPU. With
--pipelines-out it also turns the pipelines recorded on devices (pipelines/*.jsonl in the harvested folders, see
src/ios/metal_pipelines.m) into a list for the app's background warm-up, plus the compiled libraries the list
uses, named by their SHA-256 (build.sh puts them at res://port/pipelines/).

Usage: bake_shaders.py --out <dir> [--objs <dir>] [--pipelines-out <dir>] <shader_cache dir>...
"""
import argparse
import concurrent.futures as cf
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
from compression import zstd

MAGIC = 0x43535247              # RenderingShaderContainer::CONTAINER_MAGIC_NUMBER
CONTAINER = struct.Struct("<5I")  # magic, version, format, format_version, shader_count
REFLECTION_SIZE = 56            # sizeof(RenderingShaderContainer::ReflectionData)
# RenderingShaderContainerMetal::HeaderData: MetalDeviceProfile (platform, gpu, msl major, msl minor, argument
# buffers tier, simd permute + padding), msl_version, os_min_version, flags.
HEADER_OFF = CONTAINER.size + REFLECTION_SIZE
HEADER = struct.Struct("<6I3I")
SHADER = struct.Struct("<4I")       # stage, compressed size, compression flags, decompressed size
STAGE = struct.Struct("<3I32s3I")   # input mask, position invariant, fast math, sha256, source size, library size, push constant binding
PLATFORM_IOS = 1
NO_OS_MIN = 0xFFFFFFFF
# Lowest iOS that supports each Metal Shading Language version (major * 10000 + minor * 100).
MSL_MIN_IOS = {40100: (27, 0), 40000: (26, 0), 30200: (18, 0), 30100: (17, 0), 30000: (16, 0), 20400: (15, 0), 20300: (14, 0)}
APPLE6 = 1006  # MetalDeviceProfile::GPU::Apple6 (A13 and later)


def gpu_family(header):
	"""The GPU family a device needs to load the shader (Godot refuses shaders made for a newer one).

	Godot makes the Metal source from the device's features only: iOS, its Metal language version, argument buffers
	tier and SIMD permute (RenderingShaderContainerFormatMetal); the family itself is only checked when loading. Every
	GPU from Apple6 on has tier 2 argument buffers and SIMD permute (MetalDeviceProperties), so a shader made with
	both on any of them is the same on all of them: mark it Apple6 so devices older than the harvesting one use it too.
	"""
	if header[4] == 1 and header[5] & 0xFF:
		return min(header[1], APPLE6)
	return header[1]


def align4(n):
	return (n + 3) & ~3


def read_cache_file(data):
	if data[:4] != b"GDSC":
		raise ValueError("not a Godot shader cache file")
	version, count = struct.unpack_from("<2I", data, 4)
	if version != 4:
		raise ValueError(f"cache file version {version}")
	off, variants = 12, []
	for _ in range(count):
		(size,) = struct.unpack_from("<I", data, off)
		variants.append(data[off + 4:off + 4 + size])
		off += 4 + size
	if off != len(data):
		raise ValueError("trailing bytes")
	return variants


def write_cache_file(variants):
	out = bytearray(b"GDSC" + struct.pack("<2I", 4, len(variants)))
	for v in variants:
		out += struct.pack("<I", len(v)) + v
	return bytes(out)


def parse_container(blob):
	magic, _, _, _, count = CONTAINER.unpack_from(blob, 0)
	if magic != MAGIC:
		raise ValueError("bad container magic")
	header = HEADER.unpack_from(blob, HEADER_OFF)
	# The shaders are the container's tail (the reflection data before them has a variable layout); find the
	# offset from which `count` shader records end exactly at the end of the blob.
	for start in range(HEADER_OFF + HEADER.size, len(blob) - count * (SHADER.size + STAGE.size) + 1, 4):
		off, shaders = start, []
		for _ in range(count):
			if off + SHADER.size > len(blob):
				break
			stage, csize, cflags, dsize = SHADER.unpack_from(blob, off)
			code = off + SHADER.size
			end = align4(code + csize)
			if stage > 4 or cflags > 1 or not csize or not dsize or end + STAGE.size > len(blob):
				break
			if cflags and blob[code:code + 4] != b"\x28\xb5\x2f\xfd":
				break
			shaders.append(dict(stage=stage, cflags=cflags, code=blob[code:code + csize], data=list(STAGE.unpack_from(blob, end))))
			off = end + STAGE.size
		if len(shaders) == count and off == len(blob):
			return start, header, shaders
	raise ValueError("couldn't find the shader records")


def compile_options(header, stage_data):
	msl = header[6]
	major, minor = msl // 10000, (msl // 100) % 100
	ios = MSL_MIN_IOS.get(msl)
	if ios is None:
		raise ValueError(f"unknown Metal language version {msl}")
	# Same options as RenderingDeviceDriverMetal::shader_create_from_container (languageVersion, fast math,
	# preserveInvariance) and RenderingShaderContainerMetal::compile_metal_source.
	args = [f"-std=metal{major}.{minor}", f"-mios-version-min={ios[0]}.{ios[1]}", "-O3", "-fmetal-math-mode=fast"]
	if stage_data[1]:
		args.append("-fpreserve-invariance")
	return args, ios[0] * 10000 + ios[1] * 100


def compile_metal(source, args, objs):
	key = hashlib.sha256(source + b"\0" + " ".join(args).encode()).hexdigest()
	out = os.path.join(objs, key + ".metallib")
	if os.path.exists(out):
		return out
	with tempfile.TemporaryDirectory() as tmp:
		src, lib = os.path.join(tmp, "s.metal"), os.path.join(tmp, "s.metallib")
		with open(src, "wb") as f:
			f.write(source)
		r = subprocess.run(["xcrun", "-sdk", "iphoneos", "metal", *args, "-w", src, "-o", lib], capture_output=True, text=True)
		if r.returncode != 0:
			raise RuntimeError(r.stderr.strip()[-2000:])
		r = subprocess.run(["xcrun", "-sdk", "iphoneos", "metal-dsymutil", "--remove-source", lib], capture_output=True, text=True)
		if r.returncode != 0:
			raise RuntimeError(r.stderr.strip()[-2000:])
		shutil.move(lib, out + ".tmp")
		os.replace(out + ".tmp", out)
	return out


def main():
	ap = argparse.ArgumentParser()
	ap.add_argument("--out", required=True)
	ap.add_argument("--objs", help="compiled-library cache (default: <out>.objs)")
	ap.add_argument("--pipelines-out", help="where to write the warm-up list and its libraries")
	ap.add_argument("caches", nargs="+")
	a = ap.parse_args()
	objs = a.objs or a.out.rstrip("/") + ".objs"
	os.makedirs(objs, exist_ok=True)

	# Later folders win when the same file was harvested more than once.
	files, record_files = {}, []
	for root in a.caches:
		for d, _, names in os.walk(root):
			for n in names:
				if n.endswith(".metal.cache"):
					files[os.path.relpath(os.path.join(d, n), root)] = os.path.join(d, n)
				elif n.endswith(".jsonl") and os.path.basename(d) == "pipelines":
					record_files.append(os.path.join(d, n))

	jobs, plans, skipped = {}, {}, []
	for rel, path in sorted(files.items()):
		try:
			variants = read_cache_file(open(path, "rb").read())
			plan = []
			for blob in variants:
				if not blob:
					plan.append(None)
					continue
				start, header, shaders = parse_container(blob)
				if header[0] != PLATFORM_IOS:
					raise ValueError("not an iOS shader")
				stages = []
				for s in shaders:
					raw = zstd.decompress(s["code"]) if s["cflags"] else s["code"]
					if s["data"][5]:
						raise ValueError("already baked")
					source = raw[:s["data"][4]]
					args, os_min = compile_options(header, s["data"])
					k = (source, tuple(args))
					jobs.setdefault(k, None)
					stages.append((s, source, k))
				plan.append((blob, start, header, stages, os_min))
			plans[rel] = plan
		except Exception as e:
			skipped.append(f"{rel}: {e}")

	print(f"{len(plans)} shader files, {len(jobs)} distinct Metal sources to compile", flush=True)
	failed = {}
	with cf.ThreadPoolExecutor(max_workers=os.cpu_count() or 4) as pool:
		futs = {pool.submit(compile_metal, k[0], list(k[1]), objs): k for k in jobs}
		for i, f in enumerate(cf.as_completed(futs), 1):
			k = futs[f]
			try:
				jobs[k] = f.result()
			except Exception as e:
				failed[k] = str(e)
			if i % 100 == 0:
				print(f"  compiled {i}/{len(jobs)}", flush=True)

	if os.path.isdir(a.out):
		shutil.rmtree(a.out)
	baked = 0
	for rel, plan in plans.items():
		if any(k in failed for p in plan if p for _, _, k in p[3]):
			skipped.append(f"{rel}: compile failed")
			continue
		variants = []
		for p in plan:
			if p is None:
				variants.append(b"")
				continue
			blob, start, header, stages, os_min = p
			out = bytearray(blob[:start])
			h = list(header)
			h[1] = gpu_family(header)
			h[7] = os_min
			HEADER.pack_into(out, HEADER_OFF, *h)
			for s, source, k in stages:
				lib = open(jobs[k], "rb").read()
				raw = source + lib
				comp = zstd.compress(raw)
				out += SHADER.pack(s["stage"], len(comp), 1, len(raw)) + comp + b"\0" * (align4(len(comp)) - len(comp))
				d = list(s["data"])
				d[5] = len(lib)
				out += STAGE.pack(*d)
			variants.append(bytes(out))
		dest = os.path.join(a.out, rel)
		os.makedirs(os.path.dirname(dest), exist_ok=True)
		with open(dest, "wb") as f:
			f.write(write_cache_file(variants))
		baked += 1

	for line in skipped:
		print("  skipped " + line)
	for k, err in list(failed.items())[:3]:
		print("  compile error: " + err.splitlines()[-1] if err else "?")
	print(f"baked {baked} shader files into {a.out}")
	if a.pipelines_out:
		write_pipelines(a.pipelines_out, record_files, plans, jobs, failed)
	return 0 if baked else 1


def write_pipelines(out, record_files, plans, jobs, failed):
	"""The warm-up list (index.json) and the libraries it uses, from the recorded pipelines."""
	if os.path.isdir(out):
		shutil.rmtree(out)
	# A recorded library is either one Godot compiled from source ("s:<sha256 of the source>[:i]", ":i" when
	# compiled with preserveInvariance) or a baked one it loaded ("b:<sha256 of the library>").
	# What each library needs from a device for Godot to load it (shader_create_from_container): its shader's GPU
	# family, iOS version and argument buffers. The warm-up leaves out pipelines with libraries a device won't load.
	needs = {}
	for plan in plans.values():
		for p in plan:
			if p:
				_, _, header, stages, o = p
				for _, _, k in stages:
					gpu, os_min, ab = needs.get(k, (1 << 30, 1 << 30, True))
					needs[k] = (min(gpu, gpu_family(header)), min(os_min, o), ab and bool(header[8] & 2))
	libs, lib_needs = {}, {}
	for k, path in jobs.items():
		if k in failed or path is None:
			continue
		source, args = k
		bin_sha = hashlib.sha256(open(path, "rb").read()).hexdigest()
		inv = ":i" if "-fpreserve-invariance" in args else ""
		libs["s:" + hashlib.sha256(source).hexdigest() + inv] = (bin_sha, path)
		libs["b:" + bin_sha] = (bin_sha, path)
		if k in needs:
			lib_needs[bin_sha] = list(needs[k])

	records = {}
	for path in sorted(record_files):
		for n, line in enumerate(open(path, encoding="utf-8")):
			try:
				r = json.loads(line)
			except ValueError:
				continue
			old = records.get(r.get("h"))
			if old is None or r.get("t", 0) < old.get("t", 0):
				records[r["h"]] = r

	used, pipelines, missing = {}, [], 0
	for r in sorted(records.values(), key=lambda r: (r.get("t", 0), r["h"])):
		fns = [r[k] for k in ("vf", "ff", "cf") if k in r]
		if not fns or any(f["l"] not in libs for f in fns):
			missing += 1
			continue
		for f in fns:
			bin_sha, path = libs[f["l"]]
			f["l"] = bin_sha
			used[bin_sha] = path
		for k in ("h", "ms"):
			r.pop(k, None)
		pipelines.append(r)
	if not pipelines:
		print(f"no recorded pipelines to warm up ({missing} recorded with shaders that aren't in the harvest)")
		return
	os.makedirs(out)
	for bin_sha, path in used.items():
		shutil.copyfile(path, os.path.join(out, bin_sha + ".metallib"))
	index = dict(version=2, libraries={sha: lib_needs.get(sha, [0, 0, False]) for sha in used}, pipelines=pipelines)
	with open(os.path.join(out, "index.json"), "w") as f:
		json.dump(index, f, separators=(",", ":"), sort_keys=True)
	size = sum(os.path.getsize(p) for p in used.values())
	print(f"warm-up list: {len(pipelines)} pipelines using {len(used)} libraries ({size / 1048576:.1f} MB)"
		+ (f", {missing} left out (their shaders aren't in the harvest)" if missing else ""))


if __name__ == "__main__":
	sys.exit(main())
