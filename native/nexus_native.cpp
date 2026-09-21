#include <cstddef>

#if defined(_WIN32)
#define NEXUS_EXPORT extern "C" __declspec(dllexport)
#else
#define NEXUS_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// A tiny NoGC/bflat-friendly C ABI kernel. Release builds are auto-vectorized by the native compiler.
NEXUS_EXPORT float nexus_dot_f32(const float* left, const float* right, std::size_t length) noexcept {
    float sum = 0.0f;
    for (std::size_t index = 0; index < length; ++index) sum += left[index] * right[index];
    return sum;
}
