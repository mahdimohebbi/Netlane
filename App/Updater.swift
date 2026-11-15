import AppKit
import CryptoKit
import Observation

/// A published GitHub release that can be installed.
struct ReleaseInfo: Equatable, Sendable {
    let version: String
    let notes: String
    let pageURL: URL
    let dmgURL: URL
    /// Lower-case hex SHA-256 of the DMG, taken from the release asset metadata. Installs are refused without it.
    let sha256: String?
}

/// In-app updates. Netlane is distributed through GitHub Releases (not the App Store), so this checks the latest
/// release, tells the user, and on request downloads the DMG, verifies it, swaps the installed app and relaunches.
@MainActor
@Observable
final class Updater {
    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(ReleaseInfo)
        case downloading(Double)
        case installing
        case failed(String)
    }

    nonisolated static let latestReleaseURL = URL(string: "https://api.github.com/repos/mahdimohebbi/Netlane/releases/latest")!
    nonisolated static let assetName = "Netlane.dmg"

    private(set) var state: State = .idle
    /// The newest release found, kept while a download or failure replaces `state`, so the UI can still offer it.
    private(set) var latest: ReleaseInfo?

    let currentVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"

    private let prefs: Preferences
    private var lastCheck: Date?
    private var promptedVersion: String?
    private var loop: Task<Void, Never>?

    init(prefs: Preferences) {
        self.prefs = prefs
    }

    var isBusy: Bool {
        switch state {
        case .checking, .downloading, .installing: true
        default: false
        }
    }

    /// Starts the background schedule: one check shortly after launch, then at most one per day.
    func start() {
        guard loop == nil else { return }
        loop = Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            while !Task.isCancelled {
                guard let self else { return }
                if self.prefs.checkForUpdates, !self.isBusy,
                   self.lastCheck.map({ Date().timeIntervalSince($0) > 24 * 3600 }) ?? true {
                    await self.check(userInitiated: false)
                }
                try? await Task.sleep(for: .seconds(3600))
            }
        }
    }

    // MARK: - Checking

    func check(userInitiated: Bool) async {
        guard !isBusy else { return }
        state = .checking
        lastCheck = Date()
        do {
            var request = URLRequest(url: Self.latestReleaseURL, timeoutInterval: 15)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            request.setValue("Netlane/\(currentVersion)", forHTTPHeaderField: "User-Agent")
            request.cachePolicy = .reloadIgnoringLocalCacheData
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                throw UpdateError("The update server returned an unexpected response.")
            }
            guard let release = try Self.parseRelease(data), Self.isNewer(release.version, than: currentVersion) else {
                latest = nil
                state = .upToDate
                return
            }
            latest = release
            state = .available(release)
            if !userInitiated, release.version != prefs.skippedUpdateVersion, release.version != promptedVersion {
                promptedVersion = release.version
                promptToUpdate(release)
            }
        } catch {
            // Background checks fail silently (offline, rate limited); a manual check should say why.
            state = userInitiated ? .failed(Self.describe(error)) : (latest.map(State.available) ?? .idle)
        }
    }

    private func promptToUpdate(_ release: ReleaseInfo) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Netlane \(release.version) is available"
        alert.informativeText = "You have version \(currentVersion). " + Self.summary(of: release.notes)
        alert.addButton(withTitle: "Update Now")
        alert.addButton(withTitle: "Later")
        alert.addButton(withTitle: "Skip This Version")
        switch alert.runModal() {
        case .alertFirstButtonReturn: Task { await install(release) }
        case .alertThirdButtonReturn: prefs.skippedUpdateVersion = release.version
        default: break
        }
    }

    // MARK: - Installing

    func installLatest() async {
        guard let release = latest else { return }
        await install(release)
    }

    func install(_ release: ReleaseInfo) async {
        guard !isBusy else { return }
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("Netlane-update-\(UUID().uuidString)")
        do {
            guard let expected = release.sha256 else {
                throw UpdateError("This release has no checksum, so it can't be verified. Download it from the release page instead.")
            }
            let target = Bundle.main.bundleURL
            guard (try? target.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly) != true else {
                throw UpdateError("Netlane is running from a disk image. Move it to the Applications folder first, then update.")
            }
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)

            state = .downloading(0)
            let dmg = work.appendingPathComponent(Self.assetName)
            let digest = try await download(release.dmgURL, to: dmg) { [weak self] fraction in
                Task { @MainActor in
                    if case .downloading = self?.state { self?.state = .downloading(fraction) }
                }
            }
            guard digest == expected.lowercased() else {
                throw UpdateError("The downloaded file didn't match its checksum, so it was discarded.")
            }

            state = .installing
            let staged = try await Self.extractApp(from: dmg, into: work)
            try Self.validate(staged, expectedVersion: release.version)

            try InstallLocation.install(from: staged, to: target)
            try? FileManager.default.removeItem(at: work)
            InstallLocation.relaunch(target, ejecting: nil)
        } catch {
            try? FileManager.default.removeItem(at: work)
            state = .failed(Self.describe(error))
        }
    }

    private func download(_ url: URL, to file: URL, progress: @escaping @Sendable (Double) -> Void) async throws -> String {
        guard url.scheme == "https" else { throw UpdateError("The update isn't served over HTTPS.") }
        var request = URLRequest(url: url, timeoutInterval: 30)
        request.setValue("Netlane/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw UpdateError("The download failed (the server returned an error).")
        }
        let total = Double(response.expectedContentLength)
        FileManager.default.createFile(atPath: file.path, contents: nil)
        let handle = try FileHandle(forWritingTo: file)
        defer { try? handle.close() }

        var hasher = SHA256()
        var chunk = Data()
        var received = 0.0
        func flush() throws {
            guard !chunk.isEmpty else { return }
            hasher.update(data: chunk)
            try handle.write(contentsOf: chunk)
            received += Double(chunk.count)
            chunk.removeAll(keepingCapacity: true)
            if total > 0 { progress(min(received / total, 1)) }
        }
        for try await byte in bytes {
            chunk.append(byte)
            if chunk.count >= 128 * 1024 { try flush() }
        }
        try flush()
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Mounts the DMG, copies the app out to a staging folder, and always unmounts again.
    private nonisolated static func extractApp(from dmg: URL, into work: URL) async throws -> URL {
        let mount = work.appendingPathComponent("mount")
        let stage = work.appendingPathComponent("stage")
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)

        let attach = try await run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path])
        guard attach.status == 0 else { throw UpdateError("Couldn't open the downloaded disk image.") }
        defer { _ = try? runSync("/usr/bin/hdiutil", ["detach", "-quiet", "-force", mount.path]) }

        let source = mount.appendingPathComponent("Netlane.app")
        guard FileManager.default.fileExists(atPath: source.path) else {
            throw UpdateError("The disk image doesn't contain Netlane.app.")
        }
        let destination = stage.appendingPathComponent("Netlane.app")
        let copy = try await run("/usr/bin/ditto", [source.path, destination.path])
        guard copy.status == 0 else { throw UpdateError("Couldn't copy the new version out of the disk image.") }
        return destination
    }

    /// Rejects anything that isn't an intact, correctly identified, newer Netlane signed by the same team as this one.
    private nonisolated static func validate(_ app: URL, expectedVersion: String) throws {
        guard let bundle = Bundle(url: app),
              bundle.bundleIdentifier == Bundle.main.bundleIdentifier,
              let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
              version == expectedVersion
        else { throw UpdateError("The downloaded app isn't the expected version of Netlane.") }

        let verify = try runSync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        guard verify.status == 0 else { throw UpdateError("The downloaded app's signature is invalid.") }

        // Builds signed with a Developer ID can only be replaced by the same team. Ad-hoc builds carry no team to compare.
        if let current = teamIdentifier(of: Bundle.main.bundleURL), teamIdentifier(of: app) != current {
            throw UpdateError("The downloaded app was signed by a different developer.")
        }
    }

    private nonisolated static func teamIdentifier(of url: URL) -> String? {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code else { return nil }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dict = info as? [String: Any] else { return nil }
        return dict[kSecCodeInfoTeamIdentifier as String] as? String
    }

    // MARK: - Pure helpers (covered by the self-test)

    /// Parses the GitHub "latest release" payload. Returns nil when the release has no DMG for macOS.
    nonisolated static func parseRelease(_ data: Data) throws -> ReleaseInfo? {
        struct Asset: Decodable { let name: String; let browser_download_url: URL; let digest: String? }
        struct Release: Decodable { let tag_name: String; let body: String?; let html_url: URL; let assets: [Asset] }
        let release = try JSONDecoder().decode(Release.self, from: data)
        guard let asset = release.assets.first(where: { $0.name == assetName }) else { return nil }
        var sha: String?
        if let digest = asset.digest, digest.hasPrefix("sha256:") { sha = String(digest.dropFirst(7)).lowercased() }
        return ReleaseInfo(version: normalize(release.tag_name), notes: release.body ?? "", pageURL: release.html_url,
                           dmgURL: asset.browser_download_url, sha256: sha)
    }

    /// "v1.10.2-beta" -> "1.10.2"
    nonisolated static func normalize(_ tag: String) -> String {
        var s = tag.trimmingCharacters(in: .whitespaces)
        if s.lowercased().hasPrefix("v") { s.removeFirst() }
        return String(s.prefix { $0.isNumber || $0 == "." })
    }

    /// Numeric, component-wise comparison, so 1.10 > 1.9 and 1.2 == 1.2.0.
    nonisolated static func isNewer(_ candidate: String, than current: String) -> Bool {
        func parts(_ v: String) -> [Int] { normalize(v).split(separator: ".").map { Int($0) ?? 0 } }
        let a = parts(candidate), b = parts(current)
        guard !a.isEmpty else { return false }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    /// First few lines of the release notes, without the markdown noise, for the update prompt.
    nonisolated static func summary(of notes: String) -> String {
        let stop = notes.range(of: "## Downloads")?.lowerBound ?? notes.endIndex
        let lines = notes[..<stop].split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "#-* ").union(.whitespaces)).replacingOccurrences(of: "**", with: "") }
            .filter { !$0.isEmpty && $0.lowercased() != "what's new" }
        return lines.prefix(4).joined(separator: "\n")
    }

    private nonisolated static func describe(_ error: Error) -> String {
        if let error = error as? UpdateError { return error.message }
        if let url = error as? URLError, url.code == .notConnectedToInternet || url.code == .networkConnectionLost || url.code == .timedOut {
            return "Couldn't reach the update server. Check your internet connection."
        }
        return error.localizedDescription
    }

    // MARK: - Process helpers

    private nonisolated static func run(_ path: String, _ args: [String]) async throws -> (status: Int32, output: String) {
        try await Task.detached { try runSync(path, args) }.value
    }

    private nonisolated static func runSync(_ path: String, _ args: [String]) throws -> (status: Int32, output: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        try p.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}

struct UpdateError: Error {
    let message: String
    init(_ message: String) { self.message = message }
}
