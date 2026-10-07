
// ---- Appended to the exported Xcode project's dummy.cpp by build.sh ----
// fmod-gdextension calls load_all_fmod_plugins() on iOS once FMOD is initialised, but its export plugin doesn't
// generate it under Godot 4.5. We register no FMOD plugins.
//
// We also use the call (it hands us FMOD's core system, before any bank is loaded) to replace the extension's file
// callbacks. Those queue asynchronous reads on a thread of their own that holds a Godot FileAccess reference, and
// that races with FMOD closing the file: unloading the act's music banks while the music streams (after the final
// boss) crashed in RefCounted::unreference on that thread. These blocking callbacks read the banks straight out of
// the app's .pck with pread(), touch no Godot objects, and let FMOD serialise reads and closes itself.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <fcntl.h>
#include <mach-o/dyld.h>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <unordered_map>

namespace port_fmod {

enum : int { OK = 0, ERR_FILE_BAD = 13, ERR_FILE_COULDNOTSEEK = 14, ERR_FILE_EOF = 16, ERR_FILE_NOTFOUND = 18 };

struct Entry {
	uint64_t offset;
	uint64_t size;
};

struct File {
	int fd;
	bool own_fd;
	uint64_t base;
	uint64_t size;
	uint64_t pos;
};

static int pck_fd = -1;
static std::unordered_map<std::string, Entry> pck_index; // path without "res://" -> absolute offset, size
static char status[160] = "not installed";

static bool read_exact(int fd, void *buf, size_t n, uint64_t at) {
	uint8_t *p = static_cast<uint8_t *>(buf);
	while (n > 0) {
		ssize_t got = pread(fd, p, n, static_cast<off_t>(at));
		if (got <= 0) {
			return false;
		}
		p += got;
		at += static_cast<uint64_t>(got);
		n -= static_cast<size_t>(got);
	}
	return true;
}

// Godot 4 pack, format v2/v3 (see core/io/file_access_pack.cpp), unencrypted.
static bool index_pck(const char *path) {
	int fd = open(path, O_RDONLY);
	if (fd < 0) {
		snprintf(status, sizeof(status), "can't open %s", path);
		return false;
	}
	uint32_t head[6];
	uint64_t file_base = 0, dir_offset = 0;
	if (!read_exact(fd, head, sizeof(head), 0) || head[0] != 0x43504447 || head[1] < 2 || head[1] > 3 ||
			!read_exact(fd, &file_base, 8, 24)) {
		snprintf(status, sizeof(status), "unsupported pck header");
		close(fd);
		return false;
	}
	const uint32_t version = head[1], flags = head[5];
	if (flags & 1) {
		snprintf(status, sizeof(status), "encrypted pck directory");
		close(fd);
		return false;
	}
	uint64_t at = 32 + 16 * 4; // v2: directory right after the reserved header words
	if (version == 3) {
		if (!read_exact(fd, &dir_offset, 8, 32)) {
			close(fd);
			return false;
		}
		at = dir_offset;
	}
	uint32_t count = 0;
	if (!read_exact(fd, &count, 4, at)) {
		close(fd);
		return false;
	}
	at += 4;
	std::string name;
	pck_index.reserve(count);
	for (uint32_t i = 0; i < count; i++) {
		uint32_t len = 0;
		if (!read_exact(fd, &len, 4, at) || len > 4096) {
			snprintf(status, sizeof(status), "bad pck directory entry %u", i);
			pck_index.clear();
			close(fd);
			return false;
		}
		name.resize(len);
		uint64_t meta[2];
		uint32_t file_flags = 0;
		if (!read_exact(fd, &name[0], len, at + 4) || !read_exact(fd, meta, 16, at + 4 + len) ||
				!read_exact(fd, &file_flags, 4, at + 4 + len + 16 + 16)) {
			pck_index.clear();
			close(fd);
			return false;
		}
		at += 4 + len + 16 + 16 + 4;
		name.resize(strnlen(name.c_str(), len)); // paths are NUL-padded
		if (name.compare(0, 6, "res://") == 0) {
			name.erase(0, 6);
		}
		if (file_flags & 1) { // removal entry
			pck_index.erase(name);
		} else if (!(file_flags & 2)) { // skip encrypted files
			pck_index[name] = Entry{ file_base + meta[0], meta[1] };
		}
	}
	pck_fd = fd;
	return true;
}

static int open_cb(const char *name, unsigned int *filesize, void **handle, void *) {
	if (name == nullptr) {
		return ERR_FILE_NOTFOUND;
	}
	std::string path(name);
	if (path.compare(0, 6, "res://") == 0) {
		auto it = pck_index.find(path.substr(6));
		if (it == pck_index.end()) {
			return ERR_FILE_NOTFOUND;
		}
		*filesize = static_cast<unsigned int>(it->second.size);
		*handle = new File{ pck_fd, false, it->second.offset, it->second.size, 0 };
		return OK;
	}
	if (path.compare(0, 7, "user://") == 0) { // user:// is the app's Documents folder on iOS
		const char *home = getenv("HOME");
		path = std::string(home ? home : "") + "/Documents/" + path.substr(7);
	}
	int fd = open(path.c_str(), O_RDONLY);
	if (fd < 0) {
		return ERR_FILE_NOTFOUND;
	}
	struct stat st;
	if (fstat(fd, &st) != 0) {
		close(fd);
		return ERR_FILE_BAD;
	}
	*filesize = static_cast<unsigned int>(st.st_size);
	*handle = new File{ fd, true, 0, static_cast<uint64_t>(st.st_size), 0 };
	return OK;
}

static int close_cb(void *handle, void *) {
	File *f = static_cast<File *>(handle);
	if (f != nullptr) {
		if (f->own_fd) {
			close(f->fd);
		}
		delete f;
	}
	return OK;
}

static int read_cb(void *handle, void *buffer, unsigned int sizebytes, unsigned int *bytesread, void *) {
	File *f = static_cast<File *>(handle);
	*bytesread = 0;
	uint64_t want = f->pos < f->size ? f->size - f->pos : 0;
	if (want > sizebytes) {
		want = sizebytes;
	}
	uint8_t *out = static_cast<uint8_t *>(buffer);
	uint64_t done = 0;
	while (done < want) {
		ssize_t got = pread(f->fd, out + done, static_cast<size_t>(want - done), static_cast<off_t>(f->base + f->pos + done));
		if (got < 0) {
			return ERR_FILE_BAD;
		}
		if (got == 0) {
			break;
		}
		done += static_cast<uint64_t>(got);
	}
	f->pos += done;
	*bytesread = static_cast<unsigned int>(done);
	return done < sizebytes ? ERR_FILE_EOF : OK;
}

static int seek_cb(void *handle, unsigned int pos, void *) {
	File *f = static_cast<File *>(handle);
	if (pos > f->size) {
		return ERR_FILE_COULDNOTSEEK;
	}
	f->pos = pos;
	return OK;
}

typedef int (*SetFileSystemFn)(void *system, int (*)(const char *, unsigned int *, void **, void *),
		int (*)(void *, void *), int (*)(void *, void *, unsigned int, unsigned int *, void *),
		int (*)(void *, unsigned int, void *), void *asyncread, void *asynccancel, int blockalign);

static void install(void *system) {
	auto set_file_system = reinterpret_cast<SetFileSystemFn>(dlsym(RTLD_DEFAULT, "FMOD_System_SetFileSystem"));
	if (system == nullptr || set_file_system == nullptr) {
		snprintf(status, sizeof(status), "kept plugin's (no FMOD system or FMOD_System_SetFileSystem)");
		return;
	}
	char exe[4096];
	uint32_t exe_len = sizeof(exe);
	if (_NSGetExecutablePath(exe, &exe_len) != 0) {
		snprintf(status, sizeof(status), "kept plugin's (no executable path)");
		return;
	}
	std::string pck(exe);
	pck = pck.substr(0, pck.rfind('/') + 1) + "sts2.pck";
	if (!index_pck(pck.c_str())) {
		std::string reason(status);
		snprintf(status, sizeof(status), "kept plugin's (%s)", reason.c_str());
		return;
	}
	int banks = 0;
	for (const auto &e : pck_index) {
		banks += e.first.size() > 5 && e.first.compare(e.first.size() - 5, 5, ".bank") == 0;
	}
	int result = set_file_system(system, open_cb, close_cb, read_cb, seek_cb, nullptr, nullptr, -1);
	if (result != OK) {
		snprintf(status, sizeof(status), "kept plugin's (FMOD_System_SetFileSystem error %d)", result);
		return;
	}
	snprintf(status, sizeof(status), "blocking pck reads (%zu files, %d banks)", pck_index.size(), banks);
}

} // namespace port_fmod

// For the port's log (read by aot/Port PortAudio via the main program's exports).
extern "C" __attribute__((visibility("default"))) __attribute__((used)) const char *port_fmod_file_system_status() {
	return port_fmod::status;
}

extern "C" __attribute__((visibility("default"))) __attribute__((used)) uint32_t *load_all_fmod_plugins(void *p_interface, uint32_t *r_count) {
	// FMOD_IOS_INTERFACE (fmod-gdextension src/plugins/ios_plugins_loader.h): the core system is the first field.
	port_fmod::install(p_interface != nullptr ? *static_cast<void **>(p_interface) : nullptr);
	*r_count = 0;
	return static_cast<uint32_t *>(std::malloc(sizeof(uint32_t)));
}
