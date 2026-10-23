// ---- Appended to the exported Xcode project's dummy.cpp by build.sh ----
// UIScene support. Apps built with the iOS 27 SDK must use scenes (else UIKit stops them at launch), and only
// scene-based apps built with the iOS 27.1 SDK fill both screens of an iPhone Duo and resize when it opens or
// closes; older ones get a fixed iPhone-sized window. Godot 4.5 creates its window without a scene in
// application:didFinishLaunchingWithOptions:, so this scene delegate (named in Info.plist's
// UIApplicationSceneManifest by build.sh) moves that window onto the scene, keeps it the scene's size, and passes
// the scene's lifecycle on to Godot's app delegate, which UIKit no longer calls for it.
// Written against the Objective-C runtime because this file is compiled as C++.
#include <CoreGraphics/CoreGraphics.h>
#include <objc/message.h>
#include <objc/runtime.h>

namespace port_scene {

static id send(id obj, const char *sel) {
	return ((id(*)(id, SEL))objc_msgSend)(obj, sel_registerName(sel));
}

static void send_obj(id obj, const char *sel, id arg) {
	((void (*)(id, SEL, id))objc_msgSend)(obj, sel_registerName(sel), arg);
}

static id app() {
	return send((id)objc_getClass("UIApplication"), "sharedApplication");
}

static const void *window_key = &window_key;

static id get_window(id self, SEL) {
	return objc_getAssociatedObject(self, window_key);
}

static void set_window(id self, SEL, id window) {
	objc_setAssociatedObject(self, window_key, window, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

static void fit_window(id window, id scene) {
	id space = send(scene, "coordinateSpace");
	CGRect bounds = ((CGRect(*)(id, SEL))objc_msgSend)(space, sel_registerName("bounds"));
	CGRect frame = ((CGRect(*)(id, SEL))objc_msgSend)(window, sel_registerName("frame"));
	if (!CGRectEqualToRect(frame, bounds)) {
		((void (*)(id, SEL, CGRect))objc_msgSend)(window, sel_registerName("setFrame:"), bounds);
	}
}

static void will_connect(id self, SEL, id scene, id, id) {
	id window = send(send(app(), "delegate"), "window");
	if (window == nil) {
		return;
	}
	send_obj(window, "setWindowScene:", scene);
	fit_window(window, scene);
	set_window(self, nullptr, window);
	send(window, "makeKeyAndVisible");
}

static void did_update_coordinate_space(id self, SEL, id scene, id, long, id) {
	id window = get_window(self, nullptr);
	if (window != nil) {
		fit_window(window, scene);
	}
}

// Scene lifecycle -> the app delegate methods Godot implements.
static void forward(const char *app_sel) {
	id delegate = send(app(), "delegate");
	SEL sel = sel_registerName(app_sel);
	if (((BOOL(*)(id, SEL, SEL))objc_msgSend)(delegate, sel_registerName("respondsToSelector:"), sel)) {
		((void (*)(id, SEL, id))objc_msgSend)(delegate, sel, app());
	}
}

static void did_become_active(id, SEL, id) { forward("applicationDidBecomeActive:"); }
static void will_resign_active(id, SEL, id) { forward("applicationWillResignActive:"); }
static void did_enter_background(id, SEL, id) { forward("applicationDidEnterBackground:"); }
static void will_enter_foreground(id, SEL, id) { forward("applicationWillEnterForeground:"); }

__attribute__((constructor)) static void register_scene_delegate() {
	Class cls = objc_allocateClassPair(objc_getClass("UIResponder"), "PortSceneDelegate", 0);
	if (cls == nil) {
		return;
	}
	Protocol *proto = objc_getProtocol("UIWindowSceneDelegate");
	if (proto != nullptr) {
		class_addProtocol(cls, proto);
	}
	class_addMethod(cls, sel_registerName("window"), (IMP)get_window, "@@:");
	class_addMethod(cls, sel_registerName("setWindow:"), (IMP)set_window, "v@:@");
	class_addMethod(cls, sel_registerName("scene:willConnectToSession:options:"), (IMP)will_connect, "v@:@@@");
	class_addMethod(cls, sel_registerName("windowScene:didUpdateCoordinateSpace:interfaceOrientation:traitCollection:"),
			(IMP)did_update_coordinate_space, "v@:@@q@");
	class_addMethod(cls, sel_registerName("sceneDidBecomeActive:"), (IMP)did_become_active, "v@:@");
	class_addMethod(cls, sel_registerName("sceneWillResignActive:"), (IMP)will_resign_active, "v@:@");
	class_addMethod(cls, sel_registerName("sceneDidEnterBackground:"), (IMP)did_enter_background, "v@:@");
	class_addMethod(cls, sel_registerName("sceneWillEnterForeground:"), (IMP)will_enter_foreground, "v@:@");
	objc_registerClassPair(cls);
}

} // namespace port_scene
