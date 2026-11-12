// Linked into the app by build.sh. Multiplayer between phones in the same room over Wi-Fi Aware (iOS 26+): no
// router, no shared Wi-Fi network, no IP address to type.
//
// The game plays over UDP (ENet on 33771, plus the port's names and heartbeat on 33772, src/native/Port/PortNames.cs)
// and stays unchanged. Wi-Fi Aware carries those datagrams between the phones:
//  - Hosting phone: publishes the service while hosting. Each paired phone that connects gets its own two UDP sockets
//    to 127.0.0.1:33771 and :33772, so the game's host sees it as one more player on this phone.
//  - Joining phone: lists the paired phones that are hosting; joining one opens a Wi-Fi Aware connection to it and
//    UDP listeners on 127.0.0.1:33771 and :33772, and the game joins 127.0.0.1 as if the host were on this phone.
// One Wi-Fi Aware (TCP) connection per joining phone carries both ports: frames of [port index][length (2 bytes)]
// [datagram]. Phones must be paired once per pair through Apple's pairing screens (port_nearby_pair).
//
// C entry points (src/native/Port/PortNearby.cs), called on the main thread.
import DeviceDiscoveryUI
import Foundation
import Network
import SwiftUI
import UIKit
import WiFiAware

private let serviceName = "_sts2port._tcp" // Info.plist WiFiAwareServices (build.sh)
private let gamePorts: [UInt16] = [33771, 33772]
private let maxDatagram = 65_535

// MARK: C entry points

private var returned: UnsafeMutablePointer<CChar>?

/// Returns a C string that stays valid until the next call (main thread only).
private func cString(_ s: String) -> UnsafePointer<CChar> {
	free(returned)
	returned = strdup(s)
	return UnsafePointer(returned!)
}

@_cdecl("port_nearby_supported")
public func port_nearby_supported() -> Int32 {
	if #available(iOS 26.0, *) { return Nearby.supported ? 1 : 0 }
	return 0
}

@_cdecl("port_nearby_status")
public func port_nearby_status() -> UnsafePointer<CChar> {
	if #available(iOS 26.0, *) { return cString(MainActor.assumeIsolated { Nearby.shared.status }) }
	return cString("unavailable (needs iOS 26)")
}

/// Starts or stops offering this phone's hosted game to paired phones nearby.
@_cdecl("port_nearby_host")
public func port_nearby_host(_ on: Int32) {
	if #available(iOS 26.0, *) { MainActor.assumeIsolated { Nearby.shared.setHosting(on != 0) } }
}

/// Starts or stops looking for paired phones that are hosting.
@_cdecl("port_nearby_browse")
public func port_nearby_browse(_ on: Int32) {
	if #available(iOS 26.0, *) { MainActor.assumeIsolated { Nearby.shared.setBrowsing(on != 0) } }
}

/// JSON array of the hosting phones found: [{"id": "<device id>", "name": "..."}].
@_cdecl("port_nearby_hosts")
public func port_nearby_hosts() -> UnsafePointer<CChar> {
	if #available(iOS 26.0, *) { return cString(MainActor.assumeIsolated { Nearby.shared.hostsJSON }) }
	return cString("[]")
}

/// Connects to a hosting phone and starts the local relay. Poll port_nearby_join_state for the result.
@_cdecl("port_nearby_join")
public func port_nearby_join(_ device: UInt64) {
	if #available(iOS 26.0, *) { MainActor.assumeIsolated { Nearby.shared.join(device) } }
}

/// 0 not joined, 1 connecting, 2 ready (the game can join 127.0.0.1), 3 failed or disconnected.
@_cdecl("port_nearby_join_state")
public func port_nearby_join_state() -> Int32 {
	if #available(iOS 26.0, *) { return MainActor.assumeIsolated { Nearby.shared.joinState } }
	return 0
}

@_cdecl("port_nearby_leave")
public func port_nearby_leave() {
	if #available(iOS 26.0, *) { MainActor.assumeIsolated { Nearby.shared.leave() } }
}

/// Number of phones paired with this one (-1 before it's known).
@_cdecl("port_nearby_paired_count")
public func port_nearby_paired_count() -> Int32 {
	if #available(iOS 26.0, *) { return MainActor.assumeIsolated { Nearby.shared.pairedCount } }
	return 0
}

/// Shows Apple's pairing screens (make this phone visible / find the other phone).
@_cdecl("port_nearby_pair")
public func port_nearby_pair() {
	if #available(iOS 26.0, *) { MainActor.assumeIsolated { Nearby.shared.showPairing() } }
}

// MARK: frames

/// One datagram for one of the game's ports, as sent over a Wi-Fi Aware connection.
private func frame(_ port: Int, _ payload: Data) -> Data {
	var d = Data(capacity: payload.count + 3)
	d.append(UInt8(port))
	d.append(UInt8(payload.count >> 8))
	d.append(UInt8(payload.count & 0xFF))
	d.append(payload)
	return d
}

@available(iOS 26.0, *)
private typealias Link = NetworkConnection<TCP>

/// Sends frames on a Wi-Fi Aware connection in order, from any thread.
@available(iOS 26.0, *)
private final class Outbox: @unchecked Sendable {
	private let continuation: AsyncStream<Data>.Continuation
	private let task: Task<Void, Never>

	init(_ link: Link, onError: @escaping @Sendable (Error) -> Void) {
		var c: AsyncStream<Data>.Continuation!
		let stream = AsyncStream<Data>(bufferingPolicy: .bufferingNewest(512)) { c = $0 }
		continuation = c
		task = Task {
			for await data in stream {
				do { try await link.send(data) } catch {
					onError(error)
					return
				}
			}
		}
	}

	func send(_ port: Int, _ payload: Data) { continuation.yield(frame(port, payload)) }

	func close() {
		continuation.finish()
		task.cancel()
	}
}

/// Reads frames from a Wi-Fi Aware connection until it closes.
@available(iOS 26.0, *)
private func readFrames(_ link: Link, _ deliver: (Int, Data) -> Void) async throws {
	while true {
		let (head, _) = try await link.receive(exactly: 3)
		if head.count < 3 { return } // closed
		let port = Int(head[head.startIndex])
		let length = Int(head[head.startIndex + 1]) << 8 | Int(head[head.startIndex + 2])
		let (body, _) = length > 0 ? try await link.receive(exactly: length) : (Data(), nil)
		if body.count < length { return }
		if port < gamePorts.count { deliver(port, body) }
	}
}

/// Receives datagrams on a UDP connection until it's cancelled.
private func receiveLoop(_ udp: NWConnection, _ deliver: @escaping (Data) -> Void) {
	udp.receiveMessage { data, _, _, error in
		if let data, !data.isEmpty { deliver(data) }
		if error == nil, udp.state != .cancelled { receiveLoop(udp, deliver) }
	}
}

private let relayQueue = DispatchQueue(label: "port.nearby.relay", qos: .userInteractive)

// MARK: host side

/// A joining phone: its Wi-Fi Aware connection and its two UDP sockets to the game on this phone.
@available(iOS 26.0, *)
private final class HostPeer: @unchecked Sendable {
	let link: Link
	let created = ContinuousClock.now
	var udp: [NWConnection] = []
	var outbox: Outbox?
	var reader: Task<Void, Never>?
	var ready = false
	var closed = false

	init(_ link: Link) { self.link = link }

	func start(onClose: @escaping @MainActor (HostPeer) -> Void) {
		outbox = Outbox(link) { _ in Task { @MainActor in onClose(self) } }
		for (i, port) in gamePorts.enumerated() {
			let conn = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .udp)
			conn.start(queue: relayQueue)
			receiveLoop(conn) { [weak self] data in self?.outbox?.send(i, data) }
			udp.append(conn)
		}
		reader = Task { [weak self] in
			guard let self else { return }
			do {
				try await readFrames(self.link) { port, data in
					self.udp[port].send(content: data, completion: .idempotent)
				}
			} catch {}
			await MainActor.run { onClose(self) }
		}
	}

	func close() {
		guard !closed else { return }
		closed = true
		reader?.cancel()
		outbox?.close()
		udp.forEach { $0.cancel() }
	}
}

// MARK: join side

/// This phone's connection to a hosting phone, and the UDP listeners the game joins.
@available(iOS 26.0, *)
private final class JoinLink: @unchecked Sendable {
	let link: Link
	var listeners: [NWListener] = []
	/// The game's own socket for each port (the latest one that sent something).
	var game: [NWConnection?] = [nil, nil]
	var outbox: Outbox?
	var reader: Task<Void, Never>?
	var closed = false

	init(_ link: Link) { self.link = link }

	/// Listens on 127.0.0.1 for the game; throws if a port is taken (e.g. this phone is hosting).
	func listen() throws {
		for (i, port) in gamePorts.enumerated() {
			let params = NWParameters.udp
			params.allowLocalEndpointReuse = true
			params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
			let listener = try NWListener(using: params)
			listener.newConnectionHandler = { [weak self] conn in
				conn.start(queue: relayQueue)
				relayQueue.async { self?.game[i] = conn }
				receiveLoop(conn) { data in self?.outbox?.send(i, data) }
			}
			listener.start(queue: relayQueue)
			listeners.append(listener)
		}
	}

	func start(onClose: @escaping @MainActor () -> Void) {
		outbox = Outbox(link) { _ in Task { @MainActor in onClose() } }
		reader = Task { [weak self] in
			guard let self else { return }
			do {
				try await readFrames(self.link) { port, data in
					relayQueue.async { self.game[port]?.send(content: data, completion: .idempotent) }
				}
			} catch {}
			await MainActor.run { onClose() }
		}
	}

	func close() {
		guard !closed else { return }
		closed = true
		reader?.cancel()
		outbox?.close()
		listeners.forEach { $0.cancel() }
		relayQueue.async { self.game.forEach { $0?.cancel() } }
	}
}

// MARK: state

@available(iOS 26.0, *)
@MainActor
final class Nearby {
	static let shared = Nearby()
	nonisolated static var supported: Bool {
		WACapabilities.supportedFeatures.contains(.wifiAware)
			&& WAPublishableService.allServices[serviceName] != nil && WASubscribableService.allServices[serviceName] != nil
	}

	private var hosting = false
	private var browsing = false
	private var pairingShown = false
	private var listenerTask: Task<Void, Never>?
	private var browserTask: Task<Void, Never>?
	private var pairedTask: Task<Void, Never>?
	private var peers: [HostPeer] = []
	private var endpoints: [WAEndpoint] = []
	private var joinLink: JoinLink?
	private(set) var joinState: Int32 = 0
	private(set) var pairedCount: Int32 = -1
	private var listenerState = "off"
	private var browserState = "off"
	private var lastError = ""

	private init() {
		pairedTask = Task { [weak self] in
			do {
				for try await devices in WAPairedDevice.allDevices { self?.pairedCount = Int32(devices.count) }
			} catch {
				self?.lastError = "paired devices: \(error)"
			}
		}
	}

	var status: String {
		guard Nearby.supported else { return "not supported on this device" }
		let paired = pairedCount < 0 ? "?" : "\(pairedCount)"
		var parts = ["\(paired) paired"]
		if hosting { parts.append("hosting (\(listenerState), \(peers.filter { !$0.closed }.count) connected)") }
		if browsing { parts.append("looking (\(browserState), \(endpoints.count) hosting)") }
		if joinLink != nil || joinState != 0 { parts.append("joined: \(["no", "connecting", "ready", "failed"][Int(joinState)])") }
		if !lastError.isEmpty { parts.append("last error: \(lastError)") }
		return parts.joined(separator: "; ")
	}

	var hostsJSON: String {
		let list = endpoints.map { ep -> [String: String] in
			let d = ep.device
			let name = d.name ?? d.pairingInfo?.pairingName ?? d.pairingInfo?.modelName ?? "Nearby phone"
			return ["id": String(d.id), "name": name]
		}
		guard let data = try? JSONSerialization.data(withJSONObject: list) else { return "[]" }
		return String(decoding: data, as: UTF8.self)
	}

	// MARK: hosting

	func setHosting(_ on: Bool) {
		guard on != hosting, Nearby.supported else { return }
		hosting = on
		if on {
			startListener()
		} else {
			listenerTask?.cancel()
			listenerTask = nil
			listenerState = "off"
			peers.forEach { $0.close() }
			peers.removeAll()
		}
	}

	/// Publishes until hosting stops. Publishing ends by itself after a while (connections stay up): start again.
	private func startListener() {
		guard listenerTask == nil, hosting, !pairingShown, let service = WAPublishableService.allServices[serviceName] else { return }
		listenerTask = Task { [weak self] in
			while let self, self.hosting, !Task.isCancelled {
				do {
					let listener = try NetworkListener(
						for: .wifiAware(.connecting(to: service, from: .allPairedDevices, datapath: .realtime)),
						using: .parameters { TCP() }.wifiAware { $0.performanceMode = .realtime })
						.onStateUpdate { _, state in Task { @MainActor in self.listenerState = "\(state)" } }
					try await listener.run { link in self.adopt(link) }
				} catch {
					if Task.isCancelled { break }
					self.lastError = "publishing: \(error)"
				}
				try? await Task.sleep(for: .seconds(1))
			}
		}
	}

	private func adopt(_ link: Link) {
		guard hosting else { return }
		let peer = HostPeer(link)
		peers.append(peer)
		peers.removeAll { $0.closed }
		peer.start { [weak self] p in
			p.close()
			self?.peers.removeAll { $0 === p }
		}
	}

	// MARK: joining

	func setBrowsing(_ on: Bool) {
		guard on != browsing, Nearby.supported else { return }
		browsing = on
		if on {
			startBrowser()
		} else {
			browserTask?.cancel()
			browserTask = nil
			browserState = "off"
			endpoints = []
		}
	}

	private func startBrowser() {
		guard browserTask == nil, browsing, !pairingShown, let service = WASubscribableService.allServices[serviceName] else { return }
		browserTask = Task { [weak self] in
			while let self, self.browsing, !Task.isCancelled {
				do {
					let browser = NetworkBrowser(for: .wifiAware(.connecting(to: .allPairedDevices, from: service)))
						.onStateUpdate { _, state in Task { @MainActor in self.browserState = "\(state)" } }
					try await browser.run { found in Task { @MainActor in self.endpoints = found } }
				} catch {
					if Task.isCancelled { break }
					self.lastError = "looking: \(error)"
				}
				self.endpoints = []
				try? await Task.sleep(for: .seconds(1))
			}
		}
	}

	func join(_ device: UInt64) {
		leave()
		guard let ep = endpoints.first(where: { $0.device.id == device }) else {
			lastError = "that phone isn't hosting anymore"
			joinState = 3
			return
		}
		joinState = 1
		let link = NetworkConnection(to: ep, using: .parameters { TCP() }.wifiAware { $0.performanceMode = .realtime })
		let jl = JoinLink(link)
		joinLink = jl
		do {
			try jl.listen()
		} catch {
			lastError = "local relay: \(error)"
			fail(jl)
			return
		}
		link.onStateUpdate { [weak self] _, state in
			Task { @MainActor in
				guard let self, self.joinLink === jl else { return }
				switch state {
				case .ready:
					if self.joinState == 1 { self.joinState = 2 }
				case .failed(let error):
					self.lastError = "connection: \(error)"
					self.fail(jl)
				case .cancelled:
					self.fail(jl)
				default:
					break
				}
			}
		}
		jl.start { [weak self] in self?.fail(jl) }
	}

	private func fail(_ jl: JoinLink) {
		jl.close()
		if joinLink === jl {
			joinLink = nil
			joinState = 3
		}
	}

	func leave() {
		joinLink?.close()
		joinLink = nil
		joinState = 0
	}

	// MARK: pairing

	func showPairing() {
		guard Nearby.supported, !pairingShown,
		      let pub = WAPublishableService.allServices[serviceName],
		      let sub = WASubscribableService.allServices[serviceName],
		      let presenter = Nearby.topViewController() else { return }
		// Publishing or looking while Apple's pairing screens are up gets in their way.
		pairingShown = true
		listenerTask?.cancel()
		listenerTask = nil
		browserTask?.cancel()
		browserTask = nil
		let host = UIHostingController(rootView: PairingView(publish: pub, subscribe: sub) { [weak self] in
			presenter.dismiss(animated: true)
			self?.pairingClosed()
		})
		host.modalPresentationStyle = .formSheet
		host.isModalInPresentation = true
		presenter.present(host, animated: true)
	}

	private func pairingClosed() {
		pairingShown = false
		startListener()
		startBrowser()
	}

	private static func topViewController() -> UIViewController? {
		let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
		var top = scenes.flatMap(\.windows).first(where: \.isKeyWindow)?.rootViewController
		while let presented = top?.presentedViewController { top = presented }
		return top
	}
}

@available(iOS 26.0, *)
private struct PairingView: View {
	let publish: WAPublishableService
	let subscribe: WASubscribableService
	let done: () -> Void

	var body: some View {
		NavigationStack {
			List {
				Section {
					DevicePairingView(.wifiAware(.connecting(to: publish, from: .userSpecifiedDevices))) {
						Label("Make this phone visible", systemImage: "antenna.radiowaves.left.and.right")
					} fallback: {
						Label("Pairing isn't available on this phone", systemImage: "xmark.circle")
					}
				} header: {
					Text("On one phone")
				}
				Section {
					DevicePicker(.wifiAware(.connecting(to: .userSpecifiedDevices, from: subscribe)), onSelect: { _ in }) {
						Label("Find the other phone", systemImage: "magnifyingglass")
					} fallback: {
						Label("Pairing isn't available on this phone", systemImage: "xmark.circle")
					}
				} header: {
					Text("On the other phone")
				} footer: {
					Text("Pick the visible phone, then follow the prompts on both. Each pair of phones only does this once. "
						+ "After that, a phone that hosts shows up under Join on the other, with no Wi-Fi network needed.")
				}
			}
			.navigationTitle("Pair a nearby phone")
			.toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done", action: done) } }
		}
	}
}
