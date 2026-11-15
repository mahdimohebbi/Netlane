import AppKit
import Network
import Observation
import ServiceManagement
import SystemConfiguration

@MainActor
@Observable
final class AppModel {
    enum GeoState { case loading, loaded(GeoInfo), failed(String) }
    enum SpeedPhase: String { case idle = "", latency = "Measuring latency…", download = "Testing download…", upload = "Testing upload…" }

    var geoState: GeoState = .loading
    var lastGeo: GeoInfo?
    var isRefreshing = false
    var connectionType = "—"
    var localIP = "—"
    var dnsServers: [String] = []
    var vpnLikely = false
    var isOnline = true
    var ipCopied = false

    var speedPhase: SpeedPhase = .idle
    var speedResult: SpeedResult?
    var speedError: String?

    var launchAtLogin = SMAppService.mainApp.status == .enabled
    var launchAtLoginError: String?

    var downloadRate: Double = 0
    var uploadRate: Double = 0
    var onIPChange: ((GeoInfo, GeoInfo) -> Void)?
    let stats = TrafficStats()

    private let client = GeoClient()
    private let monitor = NWPathMonitor()
    private var debounceTask: Task<Void, Never>?
    private var timer: Timer?
    private var trafficTimer: Timer?
    private var lastCounters: [String: (rx: UInt32, tx: UInt32)] = [:]
    private var lastSample = Date()

    var menuBarLabel: String {
        if !isOnline { return Flag.offline }
        switch geoState {
        case .loaded(let geo): return geo.flag
        case .failed: return lastGeo?.flag ?? Flag.offline
        case .loading: return lastGeo?.flag ?? Flag.loading
        }
    }

    init() {
        monitor.pathUpdateHandler = { [weak self] path in
            Task { @MainActor in self?.pathChanged(path) }
        }
        monitor.start(queue: DispatchQueue(label: "Netlane.path"))
        timer = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
        Task { await refresh() }
    }

    private func pathChanged(_ path: NWPath) {
        isOnline = path.status == .satisfied
        if path.usesInterfaceType(.wifi) { connectionType = "Wi-Fi" }
        else if path.usesInterfaceType(.wiredEthernet) { connectionType = "Ethernet" }
        else if path.usesInterfaceType(.cellular) { connectionType = "Cellular" }
        else if isOnline { connectionType = "Other" }
        else { connectionType = "Offline" }
        refreshLocalInfo()
        debounceTask?.cancel()
        debounceTask = Task {
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled else { return }
            await refresh()
        }
    }

    func refresh() async {
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        refreshLocalInfo()
        if lastGeo == nil { geoState = .loading }
        do {
            let geo = try await client.lookup()
            if let old = lastGeo, old.ip != geo.ip { onIPChange?(old, geo) }
            lastGeo = geo
            geoState = .loaded(geo)
        } catch {
            geoState = .failed(isOnline ? error.localizedDescription : "No network connection")
        }
    }

    func refreshLocalInfo() {
        let interfaces = Self.ipv4Interfaces()
        localIP = interfaces.first { $0.name.hasPrefix("en") }?.address
            ?? interfaces.first { !$0.name.hasPrefix("lo") && !$0.name.hasPrefix("utun") }?.address ?? "—"
        vpnLikely = interfaces.contains { $0.name.hasPrefix("utun") || $0.name.hasPrefix("ipsec") || $0.name.hasPrefix("ppp") }
        dnsServers = Self.dnsServers()
    }

    func runSpeedTest() async {
        guard speedPhase == .idle else { return }
        speedError = nil
        var result = SpeedResult()
        var errors: [String] = []
        speedPhase = .latency
        do { result.latencyMs = try await SpeedTester.latency() } catch { errors.append("latency") }
        speedPhase = .download
        do { result.downloadMbps = try await SpeedTester.download() } catch { errors.append("download") }
        speedPhase = .upload
        do { result.uploadMbps = try await SpeedTester.upload() } catch { errors.append("upload") }
        speedPhase = .idle
        result.date = Date()
        if errors.count < 3 { speedResult = result }
        if !errors.isEmpty { speedError = "Failed: " + errors.joined(separator: ", ") }
    }

    func setTrafficMonitoring(_ enabled: Bool) {
        guard enabled != (trafficTimer != nil) else { return }
        trafficTimer?.invalidate()
        trafficTimer = nil
        downloadRate = 0
        uploadRate = 0
        guard enabled else { return }
        lastCounters = Self.interfaceCounters()
        lastSample = Date()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.sampleTraffic() }
        }
        RunLoop.main.add(timer, forMode: .common)
        trafficTimer = timer
    }

    private func sampleTraffic() {
        let now = Date()
        let counters = Self.interfaceCounters()
        let elapsed = now.timeIntervalSince(lastSample)
        var rx: UInt64 = 0, tx: UInt64 = 0
        for (name, current) in counters {
            guard let previous = lastCounters[name] else { continue }
            rx += UInt64(current.rx &- previous.rx) // 32-bit kernel counters; wrapping subtraction handles rollover
            tx += UInt64(current.tx &- previous.tx)
        }
        lastCounters = counters
        lastSample = now
        guard elapsed > 0 else { return }
        downloadRate = Double(rx) / elapsed
        uploadRate = Double(tx) / elapsed
        stats.record(rx: rx, tx: tx, elapsed: elapsed, at: now)
    }

    /// Byte counters for physical interfaces only, so VPN tunnels aren't counted twice.
    private static func interfaceCounters() -> [String: (rx: UInt32, tx: UInt32)] {
        var result: [String: (rx: UInt32, tx: UInt32)] = [:]
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [:] }
        defer { freeifaddrs(head) }
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = ptr.pointee
            let name = String(cString: ifa.ifa_name)
            guard name.hasPrefix("en") || name.hasPrefix("pdp_ip"),
                  let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_LINK),
                  let data = ifa.ifa_data?.assumingMemoryBound(to: if_data.self) else { continue }
            result[name] = (data.pointee.ifi_ibytes, data.pointee.ifi_obytes)
        }
        return result
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        launchAtLoginError = nil
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            launchAtLoginError = error.localizedDescription
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    func copyPublicIP(_ ip: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(ip, forType: .string)
        ipCopied = true
        Task {
            try? await Task.sleep(for: .seconds(1.2))
            ipCopied = false
        }
    }

    private static func ipv4Interfaces() -> [(name: String, address: String)] {
        var result: [(String, String)] = []
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [] }
        defer { freeifaddrs(head) }
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = ptr.pointee
            guard let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
                  (ifa.ifa_flags & UInt32(IFF_UP)) != 0 else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                result.append((String(cString: ifa.ifa_name), String(cString: host)))
            }
        }
        return result
    }

    private static func dnsServers() -> [String] {
        guard let store = SCDynamicStoreCreate(nil, "Netlane" as CFString, nil, nil),
              let dict = SCDynamicStoreCopyValue(store, "State:/Network/Global/DNS" as CFString) as? [String: Any],
              let servers = dict["ServerAddresses"] as? [String] else { return [] }
        return servers
    }
}
