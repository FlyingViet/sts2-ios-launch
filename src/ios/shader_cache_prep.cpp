
// ---- Appended to the exported Xcode project's dummy.cpp by build.sh ----
// Shader cache preparation. Runs before main(), so before Godot's renderer reads any shader.
//
// build.sh adds precompiled ("baked") shaders to the game data at res://.godot/shader_cache (tools/bake_shaders.py).
// Godot looks in its own cache in Documents first and only falls back to that folder, and the copies it saved
// there hold Metal source that it compiles on first draw (a ~0.3 s freeze per new effect whenever iOS's Metal
// cache is empty). So after each install of new game data, remove the saved copies of every baked shader. Shaders
// the bake doesn't have keep their saved copies; baked ones that don't suit a device (another GPU family or an
// older iOS) fail to load and Godot compiles and saves them as before.
//
// Test switches, each used once and removed (push an empty file to Documents with devicectl):
//   port_cold_metal    forget iOS's compiled Metal shaders (as after an iOS update)
//   port_cold_shaders  delete Godot's own shader cache in Documents too (as on a first install)
#include <algorithm>
#include <dirent.h>
#include <removefile.h>
#include <vector>

namespace port_shaders {

static char status[256] = "not run";

static bool remove_tree(const std::string &path) {
	struct stat st;
	if (lstat(path.c_str(), &st) != 0) {
		return false;
	}
	removefile_state_t state = removefile_state_alloc();
	int r = removefile(path.c_str(), state, REMOVEFILE_RECURSIVE);
	removefile_state_free(state);
	return r == 0;
}

static bool take_flag(const std::string &path) {
	return unlink(path.c_str()) == 0;
}

// Paths (relative to the shader cache folder) of the baked shaders in a Godot 4 pck (format v2/v3, unencrypted).
static bool baked_shaders(const std::string &pck, std::vector<std::string> &r_paths) {
	int fd = open(pck.c_str(), O_RDONLY);
	if (fd < 0) {
		return false;
	}
	uint32_t head[6];
	uint64_t at = 32 + 16 * 4;
	bool ok = port_fmod::read_exact(fd, head, sizeof(head), 0) && head[0] == 0x43504447 && head[1] >= 2 && head[1] <= 3 && !(head[5] & 1);
	if (ok && head[1] == 3) {
		ok = port_fmod::read_exact(fd, &at, 8, 32);
	}
	uint32_t count = 0;
	ok = ok && port_fmod::read_exact(fd, &count, 4, at);
	at += 4;
	// Read the directory in 1 MB blocks rather than with several reads per entry.
	std::vector<uint8_t> buf;
	uint64_t buf_at = 0;
	auto fetch = [&](uint64_t off, size_t n) -> const uint8_t * {
		if (off < buf_at || off + n > buf_at + buf.size()) {
			buf.resize(std::max<size_t>(n, 1 << 20));
			ssize_t got = pread(fd, buf.data(), buf.size(), static_cast<off_t>(off));
			buf.resize(got > 0 ? static_cast<size_t>(got) : 0);
			buf_at = off;
			if (buf.size() < n) {
				return nullptr;
			}
		}
		return buf.data() + (off - buf_at);
	};
	static const char prefix[] = ".godot/shader_cache/"; // pck v3 paths have no "res://"
	for (uint32_t i = 0; ok && i < count; i++) {
		const uint8_t *p = fetch(at, 4);
		uint32_t len = p ? *reinterpret_cast<const uint32_t *>(p) : UINT32_MAX;
		const uint8_t *e = len <= 4096 ? fetch(at + 4, len + 36) : nullptr;
		if (e == nullptr) {
			ok = false;
			break;
		}
		std::string name(reinterpret_cast<const char *>(e), strnlen(reinterpret_cast<const char *>(e), len));
		uint32_t flags = *reinterpret_cast<const uint32_t *>(e + len + 32);
		if (name.compare(0, 6, "res://") == 0) {
			name.erase(0, 6);
		}
		if (!(flags & 1) && name.compare(0, sizeof(prefix) - 1, prefix) == 0) {
			r_paths.push_back(name.substr(sizeof(prefix) - 1));
		}
		at += 4 + len + 36;
	}
	close(fd);
	return ok;
}

__attribute__((constructor)) static void prepare() {
	const char *home_env = getenv("HOME");
	char exe[4096];
	uint32_t exe_len = sizeof(exe);
	if (home_env == nullptr || _NSGetExecutablePath(exe, &exe_len) != 0) {
		snprintf(status, sizeof(status), "skipped (no home or executable path)");
		return;
	}
	const std::string home(home_env), docs = home + "/Documents", cache = docs + "/shader_cache";
	std::string notes;

	if (take_flag(docs + "/port_cold_metal")) {
		// iOS keeps them in Library/Caches/<bundle id>/com.apple.metal (and com.apple.metalfe).
		int removed = 0;
		const std::string caches = home + "/Library/Caches";
		if (DIR *d = opendir(caches.c_str())) {
			while (dirent *e = readdir(d)) {
				if (e->d_name[0] == '.') {
					continue;
				}
				const std::string dir = caches + "/" + e->d_name;
				for (const char *sub : { "", "/com.apple.metal", "/com.apple.metalfe" }) {
					const std::string p = dir + sub;
					if ((*sub || strncmp(e->d_name, "com.apple.metal", 15) == 0) && remove_tree(p)) {
						removed++;
					}
				}
			}
			closedir(d);
		}
		notes += "test: cleared iOS's Metal shader cache (" + std::to_string(removed) + " folders); ";
	}
	if (take_flag(docs + "/port_cold_shaders")) {
		notes += remove_tree(cache) ? "test: deleted Godot's shader cache; " : "test: no Godot shader cache to delete; ";
	}

	std::string pck(exe);
	pck = pck.substr(0, pck.rfind('/') + 1) + "sts2.pck";
	struct stat st;
	if (stat(pck.c_str(), &st) != 0) {
		snprintf(status, sizeof(status), "%sno game data at %s", notes.c_str(), pck.c_str());
		return;
	}
	const std::string stamp_path = cache + "/.port_baked", stamp = std::to_string(st.st_size) + " " + std::to_string(st.st_mtime);
	char old_stamp[64] = {};
	if (FILE *f = fopen(stamp_path.c_str(), "r")) {
		size_t n = fread(old_stamp, 1, sizeof(old_stamp) - 1, f);
		old_stamp[n] = 0;
		fclose(f);
	}
	if (stamp == old_stamp) {
		snprintf(status, sizeof(status), "%sbaked shaders already in use", notes.c_str());
		return;
	}
	std::vector<std::string> baked;
	if (!baked_shaders(pck, baked)) {
		snprintf(status, sizeof(status), "%scouldn't read the game data's directory", notes.c_str());
		return;
	}
	int removed = 0;
	for (const std::string &rel : baked) {
		removed += unlink((cache + "/" + rel).c_str()) == 0;
	}
	mkdir(cache.c_str(), 0755);
	if (FILE *f = fopen(stamp_path.c_str(), "w")) {
		fputs(stamp.c_str(), f);
		fclose(f);
	}
	snprintf(status, sizeof(status), "%s%zu baked shaders in the game data; removed %d uncompiled copies", notes.c_str(), baked.size(), removed);
}

} // namespace port_shaders

extern "C" __attribute__((visibility("default"))) __attribute__((used)) const char *port_shader_cache_status() {
	return port_shaders::status;
}
