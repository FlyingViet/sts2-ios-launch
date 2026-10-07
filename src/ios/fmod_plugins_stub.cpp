
// Added by rebuild.sh: fmod-gdextension's iOS export plugin didn't inject this under Godot 4.5.
#include <stdint.h>
#include <cstdlib>
extern "C" __attribute__((visibility("default"))) __attribute__((used)) uint32_t *load_all_fmod_plugins(void *p_interface, uint32_t *r_count) {
	*r_count = 0;
	return reinterpret_cast<uint32_t *>(std::malloc(sizeof(uint32_t)));
}
