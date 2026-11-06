// Linked into the app by build.sh (compiled on its own: the exported Xcode project's code is C++).
// Pipeline warm-up, so the first look at an effect doesn't freeze the game.
//
// Godot 4.5 creates each Metal pipeline (a shader plus its blending, attachment and vertex settings) the first time
// something is drawn with it, and its 2D renderer waits for it. When iOS hasn't compiled that pipeline for this app
// before (after installing, after an iOS update, or after iOS clears app caches), that takes ~0.3 s per new effect.
// Godot's own Metal pipeline cache is switched off in 4.5.
//  1. Recording: each pipeline the game creates is described (its shader libraries, functions, specialization
//     values, attachments, blending and vertex layout) in Documents/port_pipelines/records.jsonl, which
//     tools/harvest_shaders.sh copies off a device along with Godot's shader cache.
//  2. tools/bake_shaders.py turns the records into res://port/pipelines/: a list, plus the precompiled shader
//     libraries it uses (the same bytes Godot loads from the baked shader cache, see src/ios/shader_cache_prep.cpp).
//  3. Warm-up: right after launch, two background threads create the listed pipelines in the order the game first
//     needed them. iOS compiles and caches each one, and the game's own request then finds it ready. Devices the
//     baked shaders don't suit (older GPU families or iOS versions, which Godot compiles its own shaders for) skip it.
// It also times every shader and pipeline compile the game waits for (port_metal_status, port_metal_compile_ms),
// which src/native/PortPerf.cs logs.
//
// Test switch, used once and removed (push an empty file to Documents with devicectl): port_no_warmup skips the
// warm-up for one launch.
#import <CommonCrypto/CommonDigest.h>
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <mach-o/dyld.h>
#import <objc/runtime.h>
#import <os/lock.h>
#import <stdatomic.h>
#import <sys/sysctl.h>
#import <time.h>

static double now_ms(void) {
	return clock_gettime_nsec_np(CLOCK_UPTIME_RAW) / 1e6;
}

static double g_start_ms;
static id<MTLDevice> g_device;
static const void *kInfo = &kInfo; // associated object: library -> key, function -> description, constants -> values
static _Thread_local int t_warming; // the warm-up's own Metal calls are neither timed nor recorded

// ------------------------------------------------------------------------------------------------ timing
typedef struct {
	long n;
	double ms, max;
} Stat;
static os_unfair_lock g_lock = OS_UNFAIR_LOCK_INIT;
static Stat g_lib_src, g_lib_bin, g_fn, g_pso, g_cpso;
static double g_compile_ms;
static long g_recorded, g_unrecordable;
static char g_warm[200] = "not started";

static void add_stat(Stat *s, double ms, int compile) {
	os_unfair_lock_lock(&g_lock);
	s->n++;
	s->ms += ms;
	if (ms > s->max) {
		s->max = ms;
	}
	if (compile) {
		g_compile_ms += ms;
	}
	os_unfair_lock_unlock(&g_lock);
}

static void set_warm(NSString *text) {
	os_unfair_lock_lock(&g_lock);
	strlcpy(g_warm, text.UTF8String, sizeof(g_warm));
	os_unfair_lock_unlock(&g_lock);
}

// Total ms the game spent waiting for shader and pipeline compiles (not counting the warm-up's own work).
__attribute__((visibility("default"), used)) double port_metal_compile_ms(void) {
	os_unfair_lock_lock(&g_lock);
	double ms = g_compile_ms;
	os_unfair_lock_unlock(&g_lock);
	return ms;
}

__attribute__((visibility("default"), used)) const char *port_metal_status(void) {
	static char buf[600];
	os_unfair_lock_lock(&g_lock);
	snprintf(buf, sizeof(buf),
			"pipelines %ld (%.0f ms, max %.0f) compute %ld (%.0f ms) | shader compiles %ld (%.0f ms, max %.0f) "
			"precompiled loads %ld (%.0f ms) | specializations %ld (%.0f ms) | recorded %ld new%s | warm-up: %s",
			g_pso.n, g_pso.ms, g_pso.max, g_cpso.n, g_cpso.ms, g_lib_src.n, g_lib_src.ms, g_lib_src.max, g_lib_bin.n,
			g_lib_bin.ms, g_fn.n, g_fn.ms, g_recorded, g_unrecordable ? [NSString stringWithFormat:@" (%ld unknown)", g_unrecordable].UTF8String : "",
			g_warm);
	os_unfair_lock_unlock(&g_lock);
	return buf;
}

// ------------------------------------------------------------------------------------------------ keys
static NSString *hex(const unsigned char *d, size_t n) {
	NSMutableString *s = [NSMutableString stringWithCapacity:n * 2];
	for (size_t i = 0; i < n; i++) {
		[s appendFormat:@"%02x", d[i]];
	}
	return s;
}

static NSString *sha256_bytes(const void *p, size_t n) {
	unsigned char d[CC_SHA256_DIGEST_LENGTH];
	CC_SHA256(p, (CC_LONG)n, d);
	return hex(d, sizeof(d));
}

static NSString *sha256_dispatch(dispatch_data_t data) {
	CC_SHA256_CTX ctx, *c = &ctx;
	CC_SHA256_Init(c);
	dispatch_data_apply(data, ^bool(dispatch_data_t region, size_t offset, const void *buffer, size_t size) {
		CC_SHA256_Update(c, buffer, (CC_LONG)size);
		return true;
	});
	unsigned char d[CC_SHA256_DIGEST_LENGTH];
	CC_SHA256_Final(d, c);
	return hex(d, sizeof(d));
}

// ------------------------------------------------------------------------------------------------ hooks
// Replaces a method where the class (or the superclass that implements it) defines it. Returns the original, or
// NULL if it was already replaced.
static IMP hook(Class cls, SEL sel, IMP replacement) {
	Method m = class_getInstanceMethod(cls, sel);
	if (m == NULL || method_getImplementation(m) == replacement) {
		return NULL;
	}
	return method_setImplementation(m, replacement);
}

// Library classes can differ (and implement these methods themselves), so each implementing class keeps its own
// original.
typedef struct {
	Class cls;
	SEL sel;
	IMP orig;
} ClassHook;
static ClassHook g_class_hooks[16];
static _Atomic int g_class_hook_count;

static IMP class_orig(id self, SEL sel) {
	int n = atomic_load(&g_class_hook_count);
	for (Class c = object_getClass(self); c != Nil; c = class_getSuperclass(c)) {
		for (int i = 0; i < n; i++) {
			if (g_class_hooks[i].cls == c && g_class_hooks[i].sel == sel) {
				return g_class_hooks[i].orig;
			}
		}
	}
	return NULL;
}

static void hook_implementing_class(Class cls, SEL sel, IMP replacement) {
	Method m = class_getInstanceMethod(cls, sel);
	if (m == NULL || method_getImplementation(m) == replacement || atomic_load(&g_class_hook_count) >= 16) {
		return;
	}
	Class owner = cls;
	for (Class s = class_getSuperclass(owner); s != Nil && class_getInstanceMethod(s, sel) == m; s = class_getSuperclass(s)) {
		owner = s;
	}
	int i = atomic_load(&g_class_hook_count);
	g_class_hooks[i] = (ClassHook){ owner, sel, method_getImplementation(m) };
	atomic_store(&g_class_hook_count, i + 1); // published before the swap, so the hook always finds it
	method_setImplementation(m, replacement);
}

static void tag_function(id fn, id library, NSString *name, NSArray *constants) {
	NSString *lib = objc_getAssociatedObject(library, kInfo);
	if (fn == nil || lib == nil) {
		return;
	}
	NSArray *sorted = [constants sortedArrayUsingComparator:^NSComparisonResult(NSArray *a, NSArray *b) {
		return [a[0] compare:b[0]];
	}];
	objc_setAssociatedObject(fn, kInfo, @{ @"l" : lib, @"f" : name, @"c" : sorted ?: @[] }, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

static id hook_fn(id self, SEL _cmd, NSString *name) {
	id fn = ((id(*)(id, SEL, NSString *))class_orig(self, _cmd))(self, _cmd, name);
	if (!t_warming) {
		tag_function(fn, self, name, @[]);
	}
	return fn;
}

static id hook_fn_const(id self, SEL _cmd, NSString *name, MTLFunctionConstantValues *values, NSError **error) {
	id (*orig)(id, SEL, NSString *, MTLFunctionConstantValues *, NSError **) =
			(id(*)(id, SEL, NSString *, MTLFunctionConstantValues *, NSError **))class_orig(self, _cmd);
	if (t_warming) {
		return orig(self, _cmd, name, values, error);
	}
	double t0 = now_ms();
	id fn = orig(self, _cmd, name, values, error);
	add_stat(&g_fn, now_ms() - t0, 1);
	tag_function(fn, self, name, objc_getAssociatedObject(values, kInfo) ?: @[]);
	return fn;
}

static void tag_library(id library, NSString *key) {
	if (library == nil) {
		return;
	}
	objc_setAssociatedObject(library, kInfo, key, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
	static os_unfair_lock lock = OS_UNFAIR_LOCK_INIT;
	static Class seen[8];
	Class cls = object_getClass(library);
	os_unfair_lock_lock(&lock);
	int i = 0;
	while (i < 8 && seen[i] != Nil && seen[i] != cls) {
		i++;
	}
	if (i < 8 && seen[i] == Nil) {
		seen[i] = cls;
		hook_implementing_class(cls, @selector(newFunctionWithName:), (IMP)hook_fn);
		hook_implementing_class(cls, @selector(newFunctionWithName:constantValues:error:), (IMP)hook_fn_const);
	}
	os_unfair_lock_unlock(&lock);
}

static id (*orig_lib_src)(id, SEL, NSString *, MTLCompileOptions *, NSError **);
static id hook_lib_src(id self, SEL _cmd, NSString *source, MTLCompileOptions *options, NSError **error) {
	if (t_warming) {
		return orig_lib_src(self, _cmd, source, options, error);
	}
	double t0 = now_ms();
	id lib = orig_lib_src(self, _cmd, source, options, error);
	add_stat(&g_lib_src, now_ms() - t0, 1);
	NSData *utf8 = [source dataUsingEncoding:NSUTF8StringEncoding];
	tag_library(lib, [NSString stringWithFormat:@"s:%@%@", sha256_bytes(utf8.bytes, utf8.length), options.preserveInvariance ? @":i" : @""]);
	return lib;
}

static id (*orig_lib_bin)(id, SEL, dispatch_data_t, NSError **);
static id hook_lib_bin(id self, SEL _cmd, dispatch_data_t data, NSError **error) {
	if (t_warming) {
		return orig_lib_bin(self, _cmd, data, error);
	}
	double t0 = now_ms();
	id lib = orig_lib_bin(self, _cmd, data, error);
	add_stat(&g_lib_bin, now_ms() - t0, 0);
	tag_library(lib, [@"b:" stringByAppendingString:sha256_dispatch(data)]);
	return lib;
}

static void (*orig_set_const)(id, SEL, const void *, MTLDataType, NSUInteger);
static void hook_set_const(id self, SEL _cmd, const void *value, MTLDataType type, NSUInteger index) {
	orig_set_const(self, _cmd, value, type, index);
	if (t_warming) {
		return;
	}
	uint32_t v = 0;
	size_t size = type == MTLDataTypeBool ? 1 : (type == MTLDataTypeInt || type == MTLDataTypeUInt || type == MTLDataTypeFloat) ? 4 : 0;
	NSMutableArray *list = objc_getAssociatedObject(self, kInfo);
	if (list == nil) {
		list = [NSMutableArray new];
		objc_setAssociatedObject(self, kInfo, list, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
	}
	if (size == 0) {
		[list addObject:@[ @(index), @(type), @(-1) ]]; // a type the warm-up can't replay: the pipeline is skipped
		return;
	}
	memcpy(&v, value, size);
	[list addObject:@[ @(index), @(type), @(v) ]];
}

// ------------------------------------------------------------------------------------------------ recording
static dispatch_queue_t g_io;
static NSMutableSet<NSString *> *g_known; // record hashes already in the file (io queue only)
static NSFileHandle *g_out;
static const long kMaxRecords = 20000;

static NSString *documents(void) {
	return [NSHomeDirectory() stringByAppendingPathComponent:@"Documents"];
}

static NSString *records_path(void) {
	return [documents() stringByAppendingPathComponent:@"port_pipelines/records.jsonl"];
}

static NSDictionary *function_info(id<MTLFunction> fn) {
	return fn ? objc_getAssociatedObject(fn, kInfo) : nil;
}

static BOOL replayable(NSDictionary *fn) {
	for (NSArray *c in fn[@"c"]) {
		if ([c[2] longLongValue] < 0) {
			return NO;
		}
	}
	return YES;
}

static void record(NSMutableDictionary *r, NSString *label, double t0, double ms) {
	NSData *canon = [NSJSONSerialization dataWithJSONObject:r options:NSJSONWritingSortedKeys error:nil];
	if (canon == nil) {
		return;
	}
	NSString *h = [sha256_bytes(canon.bytes, canon.length) substringToIndex:24];
	r[@"h"] = h;
	r[@"t"] = @(llround(t0 - g_start_ms)); // ms after launch: the warm-up goes in order of first use
	r[@"ms"] = @(round(ms));
	if (label.length) {
		r[@"n"] = label;
	}
	dispatch_async(g_io, ^{
		if (g_known == nil) {
			g_known = [NSMutableSet new];
			NSString *text = [NSString stringWithContentsOfFile:records_path() encoding:NSUTF8StringEncoding error:nil];
			for (NSString *line in [text componentsSeparatedByString:@"\n"]) {
				NSRange at = [line rangeOfString:@"\"h\":\""];
				if (at.location != NSNotFound && line.length >= NSMaxRange(at) + 24) {
					[g_known addObject:[line substringWithRange:NSMakeRange(NSMaxRange(at), 24)]];
				}
			}
		}
		if ([g_known containsObject:h] || g_known.count >= kMaxRecords) {
			return;
		}
		[g_known addObject:h];
		if (g_out == nil) {
			NSString *path = records_path();
			[[NSFileManager defaultManager] createDirectoryAtPath:path.stringByDeletingLastPathComponent
									  withIntermediateDirectories:YES
													   attributes:nil
															error:nil];
			if (![[NSFileManager defaultManager] fileExistsAtPath:path]) {
				[[NSFileManager defaultManager] createFileAtPath:path contents:nil attributes:nil];
			}
			g_out = [NSFileHandle fileHandleForWritingAtPath:path];
			[g_out seekToEndOfFile];
		}
		NSMutableData *line = [[NSJSONSerialization dataWithJSONObject:r options:NSJSONWritingSortedKeys error:nil] mutableCopy];
		[line appendBytes:"\n" length:1];
		[g_out writeData:line];
		os_unfair_lock_lock(&g_lock);
		g_recorded++;
		os_unfair_lock_unlock(&g_lock);
	});
}

static void unrecordable(void) {
	os_unfair_lock_lock(&g_lock);
	g_unrecordable++;
	os_unfair_lock_unlock(&g_lock);
}

static void record_render(MTLRenderPipelineDescriptor *d, double t0, double ms) {
	NSDictionary *vf = function_info(d.vertexFunction), *ff = function_info(d.fragmentFunction);
	if ((d.vertexFunction && (!vf || !replayable(vf))) || (d.fragmentFunction && (!ff || !replayable(ff)))) {
		unrecordable();
		return;
	}
	NSMutableDictionary *r = [NSMutableDictionary dictionaryWithObject:@"r" forKey:@"k"];
	if (vf) {
		r[@"vf"] = vf;
	}
	if (ff) {
		r[@"ff"] = ff;
	}
	NSMutableArray *ca = [NSMutableArray new];
	for (NSUInteger i = 0; i < 8; i++) {
		MTLRenderPipelineColorAttachmentDescriptor *a = d.colorAttachments[i];
		if (a.pixelFormat != MTLPixelFormatInvalid) {
			[ca addObject:@[ @(i), @(a.pixelFormat), @(a.blendingEnabled), @(a.sourceRGBBlendFactor), @(a.destinationRGBBlendFactor),
				@(a.rgbBlendOperation), @(a.sourceAlphaBlendFactor), @(a.destinationAlphaBlendFactor), @(a.alphaBlendOperation),
				@(a.writeMask) ]];
		}
	}
	r[@"ca"] = ca;
	r[@"d"] = @(d.depthAttachmentPixelFormat);
	r[@"s"] = @(d.stencilAttachmentPixelFormat);
	MTLVertexDescriptor *vd = d.vertexDescriptor;
	if (vd != nil) {
		NSMutableArray *attrs = [NSMutableArray new], *layouts = [NSMutableArray new];
		for (NSUInteger i = 0; i < 31; i++) {
			MTLVertexAttributeDescriptor *a = vd.attributes[i];
			if (a.format != MTLVertexFormatInvalid) {
				[attrs addObject:@[ @(i), @(a.format), @(a.offset), @(a.bufferIndex) ]];
			}
			MTLVertexBufferLayoutDescriptor *l = vd.layouts[i];
			if (l.stride != 0) {
				[layouts addObject:@[ @(i), @(l.stride), @(l.stepFunction), @(l.stepRate) ]];
			}
		}
		r[@"va"] = attrs;
		r[@"vl"] = layouts;
	}
	r[@"topo"] = @(d.inputPrimitiveTopology);
	r[@"ras"] = @(d.rasterizationEnabled);
	r[@"sc"] = @(d.rasterSampleCount);
	r[@"a2c"] = @(d.alphaToCoverageEnabled);
	r[@"a2o"] = @(d.alphaToOneEnabled);
	record(r, d.label, t0, ms);
}

static void record_compute(MTLComputePipelineDescriptor *d, MTLPipelineOption options, double t0, double ms) {
	NSDictionary *cf = function_info(d.computeFunction);
	if (!cf || !replayable(cf)) {
		unrecordable();
		return;
	}
	NSMutableDictionary *r = [@{ @"k" : @"c", @"cf" : cf, @"tg" : @(d.threadGroupSizeIsMultipleOfThreadExecutionWidth),
		@"mt" : @(d.maxTotalThreadsPerThreadgroup), @"opt" : @(options) } mutableCopy];
	record(r, d.label, t0, ms);
}

static id (*orig_pso)(id, SEL, MTLRenderPipelineDescriptor *, NSError **);
static id hook_pso(id self, SEL _cmd, MTLRenderPipelineDescriptor *desc, NSError **error) {
	if (t_warming) {
		return orig_pso(self, _cmd, desc, error);
	}
	double t0 = now_ms();
	id state = orig_pso(self, _cmd, desc, error);
	double ms = now_ms() - t0;
	add_stat(&g_pso, ms, 1);
	if (state != nil) {
		@try {
			record_render(desc, t0, ms);
		} @catch (NSException *e) {
			unrecordable();
		}
	}
	return state;
}

static id (*orig_cpso)(id, SEL, MTLComputePipelineDescriptor *, MTLPipelineOption, MTLAutoreleasedComputePipelineReflection *, NSError **);
static id hook_cpso(id self, SEL _cmd, MTLComputePipelineDescriptor *desc, MTLPipelineOption options,
		MTLAutoreleasedComputePipelineReflection *reflection, NSError **error) {
	if (t_warming) {
		return orig_cpso(self, _cmd, desc, options, reflection, error);
	}
	double t0 = now_ms();
	id state = orig_cpso(self, _cmd, desc, options, reflection, error);
	double ms = now_ms() - t0;
	add_stat(&g_cpso, ms, 1);
	if (state != nil && reflection == NULL) {
		@try {
			record_compute(desc, options, t0, ms);
		} @catch (NSException *e) {
			unrecordable();
		}
	}
	return state;
}

// ------------------------------------------------------------------------------------------------ game data
// The warm-up's files in the app's Godot pack (format v2/v3, unencrypted; see src/ios/fmod_plugins_stub.cpp).
static int g_pck = -1;
static NSDictionary<NSString *, NSArray<NSNumber *> *> *g_files; // path under port/pipelines/ -> @[offset, size]

static BOOL read_at(int fd, void *buf, size_t n, uint64_t at) {
	uint8_t *p = buf;
	while (n > 0) {
		ssize_t got = pread(fd, p, n, (off_t)at);
		if (got <= 0) {
			return NO;
		}
		p += got;
		at += (uint64_t)got;
		n -= (size_t)got;
	}
	return YES;
}

static BOOL index_pck(NSString *path) {
	int fd = open(path.fileSystemRepresentation, O_RDONLY);
	if (fd < 0) {
		return NO;
	}
	uint32_t head[6];
	uint64_t file_base = 0, at = 32 + 16 * 4;
	if (!read_at(fd, head, sizeof(head), 0) || head[0] != 0x43504447 || head[1] < 2 || head[1] > 3 || (head[5] & 1) ||
			!read_at(fd, &file_base, 8, 24) || (head[1] == 3 && !read_at(fd, &at, 8, 32))) {
		close(fd);
		return NO;
	}
	uint32_t count = 0;
	if (!read_at(fd, &count, 4, at)) {
		close(fd);
		return NO;
	}
	at += 4;
	NSMutableDictionary *files = [NSMutableDictionary new];
	char name[4097];
	static const char prefix[] = "port/pipelines/";
	for (uint32_t i = 0; i < count; i++) {
		uint32_t len = 0, flags = 0;
		uint64_t meta[2];
		if (!read_at(fd, &len, 4, at) || len > 4096 || !read_at(fd, name, len, at + 4) || !read_at(fd, meta, 16, at + 4 + len) ||
				!read_at(fd, &flags, 4, at + 4 + len + 32)) {
			close(fd);
			return NO;
		}
		at += 4 + len + 36;
		name[len] = 0;
		const char *n = strncmp(name, "res://", 6) == 0 ? name + 6 : name;
		if (strncmp(n, prefix, sizeof(prefix) - 1) == 0 && !(flags & 3)) {
			files[@(n + sizeof(prefix) - 1)] = @[ @(file_base + meta[0]), @(meta[1]) ];
		}
	}
	g_pck = fd;
	g_files = files;
	return YES;
}

static NSData *pck_file(NSString *name) {
	NSArray<NSNumber *> *e = g_files[name];
	if (e == nil) {
		return nil;
	}
	NSMutableData *data = [NSMutableData dataWithLength:e[1].unsignedLongLongValue];
	return read_at(g_pck, data.mutableBytes, data.length, e[0].unsignedLongLongValue) ? data : nil;
}

// ------------------------------------------------------------------------------------------------ warm-up
@interface PortWarmup : NSObject {
@public
	NSArray<NSDictionary *> *list;
	NSMutableDictionary<NSString *, id> *libraries, *functions;
	os_unfair_lock lock;
	_Atomic long next, compiled, cached, failed;
	long other;
	double started;
}
@end
@implementation PortWarmup
@end

static id<MTLLibrary> warm_library(PortWarmup *w, NSString *sha) {
	os_unfair_lock_lock(&w->lock);
	id lib = w->libraries[sha];
	os_unfair_lock_unlock(&w->lock);
	if (lib != nil) {
		return lib == [NSNull null] ? nil : lib;
	}
	NSData *bytes = pck_file([sha stringByAppendingString:@".metallib"]);
	if (bytes != nil) {
		dispatch_data_t data = dispatch_data_create(bytes.bytes, bytes.length, nil, DISPATCH_DATA_DESTRUCTOR_DEFAULT);
		lib = [g_device newLibraryWithData:data error:nil];
	}
	os_unfair_lock_lock(&w->lock);
	w->libraries[sha] = lib ?: [NSNull null];
	os_unfair_lock_unlock(&w->lock);
	return lib;
}

static id<MTLFunction> warm_function(PortWarmup *w, NSDictionary *info) {
	if (info == nil) {
		return nil;
	}
	NSString *key = [NSString stringWithFormat:@"%@|%@|%@", info[@"l"], info[@"f"], info[@"c"]];
	os_unfair_lock_lock(&w->lock);
	id fn = w->functions[key];
	os_unfair_lock_unlock(&w->lock);
	if (fn != nil) {
		return fn == [NSNull null] ? nil : fn;
	}
	id<MTLLibrary> lib = warm_library(w, info[@"l"]);
	NSArray *constants = info[@"c"];
	if (lib != nil && constants.count == 0) {
		fn = [lib newFunctionWithName:info[@"f"]];
	} else if (lib != nil) {
		MTLFunctionConstantValues *values = [MTLFunctionConstantValues new];
		for (NSArray *c in constants) {
			uint32_t v = (uint32_t)[c[2] unsignedLongLongValue];
			[values setConstantValue:&v type:(MTLDataType)[c[1] unsignedIntegerValue] atIndex:[c[0] unsignedIntegerValue]];
		}
		fn = [lib newFunctionWithName:info[@"f"] constantValues:values error:nil];
	}
	os_unfair_lock_lock(&w->lock);
	w->functions[key] = fn ?: [NSNull null];
	os_unfair_lock_unlock(&w->lock);
	return fn;
}

// Creates one listed pipeline. Returns the pipeline step's time in ms (libraries and functions excluded), or -1 if
// it couldn't be made.
static double warm_one(PortWarmup *w, NSDictionary *r) {
	double t0;
	id state = nil;
	if ([r[@"k"] isEqual:@"c"]) {
		MTLComputePipelineDescriptor *d = [MTLComputePipelineDescriptor new];
		d.computeFunction = warm_function(w, r[@"cf"]);
		if (d.computeFunction == nil) {
			return -1;
		}
		d.threadGroupSizeIsMultipleOfThreadExecutionWidth = [r[@"tg"] boolValue];
		d.maxTotalThreadsPerThreadgroup = [r[@"mt"] unsignedIntegerValue];
		d.label = r[@"n"];
		t0 = now_ms();
		state = [g_device newComputePipelineStateWithDescriptor:d options:[r[@"opt"] unsignedIntegerValue] reflection:nil error:nil];
	} else {
		MTLRenderPipelineDescriptor *d = [MTLRenderPipelineDescriptor new];
		if (r[@"vf"] && !(d.vertexFunction = warm_function(w, r[@"vf"]))) {
			return -1;
		}
		if (r[@"ff"] && !(d.fragmentFunction = warm_function(w, r[@"ff"]))) {
			return -1;
		}
		for (NSArray *c in r[@"ca"]) {
			MTLRenderPipelineColorAttachmentDescriptor *a = d.colorAttachments[[c[0] unsignedIntegerValue]];
			a.pixelFormat = [c[1] unsignedIntegerValue];
			a.blendingEnabled = [c[2] boolValue];
			a.sourceRGBBlendFactor = [c[3] unsignedIntegerValue];
			a.destinationRGBBlendFactor = [c[4] unsignedIntegerValue];
			a.rgbBlendOperation = [c[5] unsignedIntegerValue];
			a.sourceAlphaBlendFactor = [c[6] unsignedIntegerValue];
			a.destinationAlphaBlendFactor = [c[7] unsignedIntegerValue];
			a.alphaBlendOperation = [c[8] unsignedIntegerValue];
			a.writeMask = [c[9] unsignedIntegerValue];
		}
		d.depthAttachmentPixelFormat = [r[@"d"] unsignedIntegerValue];
		d.stencilAttachmentPixelFormat = [r[@"s"] unsignedIntegerValue];
		if (r[@"va"]) {
			MTLVertexDescriptor *vd = [MTLVertexDescriptor vertexDescriptor];
			for (NSArray *a in r[@"va"]) {
				MTLVertexAttributeDescriptor *ad = vd.attributes[[a[0] unsignedIntegerValue]];
				ad.format = [a[1] unsignedIntegerValue];
				ad.offset = [a[2] unsignedIntegerValue];
				ad.bufferIndex = [a[3] unsignedIntegerValue];
			}
			for (NSArray *l in r[@"vl"]) {
				MTLVertexBufferLayoutDescriptor *ld = vd.layouts[[l[0] unsignedIntegerValue]];
				ld.stride = [l[1] unsignedIntegerValue];
				ld.stepFunction = [l[2] unsignedIntegerValue];
				ld.stepRate = [l[3] unsignedIntegerValue];
			}
			d.vertexDescriptor = vd;
		}
		d.inputPrimitiveTopology = [r[@"topo"] unsignedIntegerValue];
		d.rasterizationEnabled = [r[@"ras"] boolValue];
		d.rasterSampleCount = [r[@"sc"] unsignedIntegerValue];
		d.alphaToCoverageEnabled = [r[@"a2c"] boolValue];
		d.alphaToOneEnabled = [r[@"a2o"] boolValue];
		d.label = r[@"n"];
		t0 = now_ms();
		state = [g_device newRenderPipelineStateWithDescriptor:d error:nil];
	}
	return state != nil ? now_ms() - t0 : -1;
}

static const double kCachedMs = 15; // a pipeline iOS already compiled takes a few ms; a compile takes 30-500

static void warm_progress(PortWarmup *w, BOOL done) {
	set_warm([NSString stringWithFormat:@"%@ %ld/%lu pipelines in %.1f s (%ld compiled, %ld already compiled%@%@)",
				 done ? @"done," : @"running,", w->compiled + w->cached + w->failed, (unsigned long)w->list.count,
				 (now_ms() - w->started) / 1000, (long)w->compiled, (long)w->cached,
				 w->failed ? [NSString stringWithFormat:@", %ld failed", (long)w->failed] : @"",
				 w->other ? [NSString stringWithFormat:@"; %ld listed for other devices", w->other] : @""]);
}

static void warm_worker(PortWarmup *w) {
	t_warming = 1;
	for (;;) {
		long i = atomic_fetch_add(&w->next, 1);
		if (i >= (long)w->list.count) {
			break;
		}
		@autoreleasepool {
			double ms = -1;
			@try {
				ms = warm_one(w, w->list[i]);
			} @catch (NSException *e) {
			}
			atomic_fetch_add(ms < 0 ? &w->failed : ms < kCachedMs ? &w->cached : &w->compiled, 1);
		}
		if (i % 20 == 0) {
			warm_progress(w, NO);
		}
	}
	t_warming = 0;
}

static NSString *os_build(void) {
	char b[64] = "";
	size_t n = sizeof(b);
	sysctlbyname("kern.osversion", b, &n, NULL, 0);
	return @(b);
}

static void warm_up(void) {
	NSString *flag = [documents() stringByAppendingPathComponent:@"port_no_warmup"];
	if ([[NSFileManager defaultManager] removeItemAtPath:flag error:nil]) {
		set_warm(@"skipped (test switch port_no_warmup)");
		return;
	}
	char exe[4096];
	uint32_t exe_len = sizeof(exe);
	if (_NSGetExecutablePath(exe, &exe_len) != 0) {
		set_warm(@"skipped (no executable path)");
		return;
	}
	NSString *pck = [@(exe).stringByDeletingLastPathComponent stringByAppendingPathComponent:@"sts2.pck"];
	if (!index_pck(pck)) {
		set_warm(@"skipped (couldn't read the game data's directory)");
		return;
	}
	NSData *json = pck_file(@"index.json");
	NSDictionary *index = json ? [NSJSONSerialization JSONObjectWithData:json options:0 error:nil] : nil;
	NSArray *all = index[@"pipelines"];
	if (all.count == 0) {
		set_warm(@"no pipeline list in this build");
		return;
	}
	// Godot only uses a baked shader (which the list's libraries are) on devices that suit it; elsewhere it compiles
	// its own, and warming those pipelines would only cost time. Same checks as shader_create_from_container:
	// libraries -> [GPU family, minimum iOS (major * 10000 + minor * 100), uses argument buffers].
	NSOperatingSystemVersion os = NSProcessInfo.processInfo.operatingSystemVersion;
	long os_version = os.majorVersion * 10000 + os.minorVersion * 100;
	NSMutableSet *usable = [NSMutableSet new];
	[(NSDictionary *)index[@"libraries"] enumerateKeysAndObjectsUsingBlock:^(NSString *sha, NSArray *needs, BOOL *stop) {
		long family = [needs[0] longValue];
		if ((family == 0 || [g_device supportsFamily:(MTLGPUFamily)family]) && os_version >= [needs[1] longValue] &&
				(![needs[2] boolValue] || g_device.argumentBuffersSupport >= MTLArgumentBuffersTier2)) {
			[usable addObject:sha];
		}
	}];
	NSMutableArray *list = [NSMutableArray new];
	for (NSDictionary *r in all) {
		BOOL ok = YES;
		for (NSString *k in @[ @"vf", @"ff", @"cf" ]) {
			ok = ok && (r[k] == nil || [usable containsObject:r[k][@"l"]]);
		}
		if (ok) {
			[list addObject:r];
		}
	}
	if (list.count == 0) {
		set_warm(@"skipped (this device gets its own shaders, not the baked ones)");
		return;
	}

	PortWarmup *w = [PortWarmup new];
	w->list = list;
	w->libraries = [NSMutableDictionary new];
	w->functions = [NSMutableDictionary new];
	w->lock = OS_UNFAIR_LOCK_INIT;
	w->started = now_ms();
	w->other = (long)(all.count - list.count);

	// Once a full warm-up has run for this list and iOS version, probe a few pipelines and stop if iOS still has them.
	NSString *stamp_path = [documents() stringByAppendingPathComponent:@"port_pipelines/warm.stamp"];
	NSString *stamp = [NSString stringWithFormat:@"%@ %@", sha256_bytes(json.bytes, json.length), os_build()];
	if ([[NSString stringWithContentsOfFile:stamp_path encoding:NSUTF8StringEncoding error:nil] isEqual:stamp]) {
		t_warming = 1;
		long probes = MIN(6, (long)list.count), fast = 0;
		for (long i = 0; i < probes; i++) {
			@autoreleasepool {
				double ms = warm_one(w, list[(i * list.count) / probes]);
				fast += ms >= 0 && ms < kCachedMs;
			}
		}
		t_warming = 0;
		if (fast == probes) {
			set_warm([NSString stringWithFormat:@"already done for this build and iOS (%ld of %ld probes compiled before)", fast, probes]);
			return;
		}
	}

	warm_progress(w, NO);
	dispatch_group_t group = dispatch_group_create();
	dispatch_queue_t q = dispatch_get_global_queue(QOS_CLASS_UTILITY, 0);
	for (int i = 0; i < 2; i++) {
		dispatch_group_async(group, q, ^{
			warm_worker(w);
		});
	}
	dispatch_group_wait(group, DISPATCH_TIME_FOREVER);
	warm_progress(w, YES);
	if (w->failed * 10 < (long)list.count) {
		[[NSFileManager defaultManager] createDirectoryAtPath:stamp_path.stringByDeletingLastPathComponent
								  withIntermediateDirectories:YES
												   attributes:nil
														error:nil];
		[stamp writeToFile:stamp_path atomically:YES encoding:NSUTF8StringEncoding error:nil];
	}
}

__attribute__((constructor)) static void port_metal_pipelines(void) {
	g_start_ms = now_ms();
	g_device = MTLCreateSystemDefaultDevice();
	if (g_device == nil) {
		return;
	}
	g_io = dispatch_queue_create("port.pipelines.records", dispatch_queue_attr_make_with_qos_class(DISPATCH_QUEUE_SERIAL, QOS_CLASS_UTILITY, 0));
	Class device_class = object_getClass(g_device);
	orig_lib_src = (id(*)(id, SEL, NSString *, MTLCompileOptions *, NSError **))hook(device_class, @selector(newLibraryWithSource:options:error:), (IMP)hook_lib_src);
	orig_lib_bin = (id(*)(id, SEL, dispatch_data_t, NSError **))hook(device_class, @selector(newLibraryWithData:error:), (IMP)hook_lib_bin);
	orig_pso = (id(*)(id, SEL, MTLRenderPipelineDescriptor *, NSError **))hook(device_class, @selector(newRenderPipelineStateWithDescriptor:error:), (IMP)hook_pso);
	orig_cpso = (id(*)(id, SEL, MTLComputePipelineDescriptor *, MTLPipelineOption, MTLAutoreleasedComputePipelineReflection *, NSError **))
			hook(device_class, @selector(newComputePipelineStateWithDescriptor:options:reflection:error:), (IMP)hook_cpso);
	Class values_class = object_getClass([MTLFunctionConstantValues new]);
	orig_set_const = (void (*)(id, SEL, const void *, MTLDataType, NSUInteger))hook(values_class, @selector(setConstantValue:type:atIndex:), (IMP)hook_set_const);
	if (!orig_lib_src || !orig_lib_bin || !orig_pso || !orig_cpso || !orig_set_const) {
		set_warm(@"skipped (Metal hooks unavailable)");
		return;
	}
	dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
		@autoreleasepool {
			warm_up();
		}
	});
}
