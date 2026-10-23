// iOS Simulator builds only (build.sh --simulator): linked into the Simulator app, never into device builds.
// The Simulator's Metal is more limited than any iPhone's, so Godot's Metal renderer needs a few stand-ins to start
// there. Fine for checking layouts (e.g. on the iPhone Duo Simulator); not for judging how the game looks or runs.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <objc/runtime.h>

// No MetalFX in the Simulator SDK; Godot only asks whether its upscalers are supported.
@interface MTLFXSpatialScalerDescriptor : NSObject
@end
@implementation MTLFXSpatialScalerDescriptor
+ (BOOL)supportsDevice:(id)device {
	return NO;
}
@end
@interface MTLFXTemporalScalerDescriptor : NSObject
@end
@implementation MTLFXTemporalScalerDescriptor
+ (BOOL)supportsDevice:(id)device {
	return NO;
}
+ (float)supportedInputContentMinScaleForDevice:(id)device {
	return 1.0f;
}
+ (float)supportedInputContentMaxScaleForDevice:(id)device {
	return 1.0f;
}
@end

// The Simulator GPU reports GPU family Apple2; Godot's Metal renderer requires Apple4. Report Apple4 (but not
// Apple3, so Godot doesn't count on cube-map arrays, which the Simulator rejects).
static BOOL (*orig_supports_family)(id, SEL, MTLGPUFamily);
static BOOL port_supports_family(id self, SEL _cmd, MTLGPUFamily family) {
	return orig_supports_family(self, _cmd, family) || family == MTLGPUFamilyApple4;
}

// Godot still makes placeholder cube-map-array textures, which only 3D shaders read: make them plain cube maps.
static id (*orig_new_texture)(id, SEL, MTLTextureDescriptor *);
static id port_new_texture(id self, SEL _cmd, MTLTextureDescriptor *desc) {
	if (desc.textureType == MTLTextureTypeCubeArray) {
		desc = [desc copy];
		desc.textureType = MTLTextureTypeCube;
		desc.arrayLength = 1;
	}
	return orig_new_texture(self, _cmd, desc);
}
static id (*orig_view)(id, SEL, MTLPixelFormat, MTLTextureType, NSRange, NSRange);
static id port_view(id self, SEL _cmd, MTLPixelFormat format, MTLTextureType type, NSRange levels, NSRange slices) {
	if (type == MTLTextureTypeCubeArray) {
		type = MTLTextureTypeCube;
		slices = NSMakeRange(0, 6);
	}
	return orig_view(self, _cmd, format, type, levels, slices);
}
static id (*orig_view_swizzle)(id, SEL, MTLPixelFormat, MTLTextureType, NSRange, NSRange, MTLTextureSwizzleChannels);
static id port_view_swizzle(id self, SEL _cmd, MTLPixelFormat format, MTLTextureType type, NSRange levels,
		NSRange slices, MTLTextureSwizzleChannels swizzle) {
	if (type == MTLTextureTypeCubeArray) {
		type = MTLTextureTypeCube;
		slices = NSMakeRange(0, 6);
	}
	return orig_view_swizzle(self, _cmd, format, type, levels, slices, swizzle);
}

// No presentDrawable:afterMinimumDuration: (Godot's frame pacing) either: present right away.
static void port_present_after(id self, SEL _cmd, id drawable, CFTimeInterval duration) {
	[(id<MTLCommandBuffer>)self presentDrawable:drawable];
}

__attribute__((constructor)) static void port_simulator_metal(void) {
	id<MTLDevice> device = MTLCreateSystemDefaultDevice();
	if (device == nil) {
		return;
	}
	Class device_class = object_getClass(device);
	Method m = class_getInstanceMethod(device_class, @selector(supportsFamily:));
	orig_supports_family = (BOOL(*)(id, SEL, MTLGPUFamily))method_setImplementation(m, (IMP)port_supports_family);
	m = class_getInstanceMethod(device_class, @selector(newTextureWithDescriptor:));
	orig_new_texture = (id(*)(id, SEL, MTLTextureDescriptor *))method_setImplementation(m, (IMP)port_new_texture);

	MTLTextureDescriptor *desc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm
																					 width:1
																					height:1
																				 mipmapped:NO];
	Class texture_class = object_getClass([device newTextureWithDescriptor:desc]);
	m = class_getInstanceMethod(texture_class, @selector(newTextureViewWithPixelFormat:textureType:levels:slices:));
	if (m != NULL) {
		orig_view = (id(*)(id, SEL, MTLPixelFormat, MTLTextureType, NSRange, NSRange))method_setImplementation(m, (IMP)port_view);
	}
	m = class_getInstanceMethod(texture_class, @selector(newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:));
	if (m != NULL) {
		orig_view_swizzle = (id(*)(id, SEL, MTLPixelFormat, MTLTextureType, NSRange, NSRange, MTLTextureSwizzleChannels))
				method_setImplementation(m, (IMP)port_view_swizzle);
	}

	Class buffer_class = object_getClass([[device newCommandQueue] commandBuffer]);
	if (![buffer_class instancesRespondToSelector:@selector(presentDrawable:afterMinimumDuration:)]) {
		class_addMethod(buffer_class, @selector(presentDrawable:afterMinimumDuration:), (IMP)port_present_after, "v@:@d");
	}
}
