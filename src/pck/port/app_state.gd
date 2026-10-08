extends Node
# Relays app focus and background changes the moment iOS reports them (Godot delivers these notifications from
# inside the UIKit callbacks, before the next frame). src/native/Port/PortKeepAlive.cs listens, to keep multiplayer
# games running when the app isn't in front.

signal focus_lost
signal focus_regained
signal entered_background
signal left_background


func _notification(what: int) -> void:
	match what:
		NOTIFICATION_APPLICATION_FOCUS_OUT:
			focus_lost.emit()
		NOTIFICATION_APPLICATION_FOCUS_IN:
			focus_regained.emit()
		NOTIFICATION_APPLICATION_PAUSED:
			entered_background.emit()
		NOTIFICATION_APPLICATION_RESUMED:
			left_background.emit()
