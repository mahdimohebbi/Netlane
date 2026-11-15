import AppKit

/// Detects when Netlane is running from a mounted disk image (or another temporary location such as
/// Downloads / an App Translocation path) and offers to move it into /Applications. Without this, users
/// who launch straight from the DMG lose the app as soon as they eject the "Netlane" disk.
@MainActor
enum InstallLocation {
    /// Returns true if the app is being relaunched from /Applications and the caller should not continue startup.
    static func moveToApplicationsIfNeeded() -> Bool {
        let source = originalBundleURL()
        guard needsMove(source) else { return false }

        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Move Netlane to the Applications folder?"
        alert.informativeText = isOnReadOnlyVolume(source)
            ? "Netlane is running from the disk image. If you eject the “Netlane” disk, the app will disappear. Move it to Applications so it stays installed."
            : "Netlane works best from the Applications folder. It can move itself there now."
        alert.addButton(withTitle: "Move to Applications")
        alert.addButton(withTitle: "Do Not Move")
        guard alert.runModal() == .alertFirstButtonReturn else { return false }

        let destination = URL(fileURLWithPath: "/Applications").appendingPathComponent(source.lastPathComponent)
        do {
            try install(from: source, to: destination)
        } catch {
            let fail = NSAlert()
            fail.messageText = "Couldn’t move Netlane to Applications"
            fail.informativeText = "\(error.localizedDescription)\n\nDrag Netlane onto the Applications folder in the disk image window instead."
            fail.runModal()
            return false
        }

        relaunch(destination, ejecting: isOnReadOnlyVolume(source) ? volumeURL(of: source) : nil)
        return true
    }

    // MARK: - Detection

    private static func needsMove(_ url: URL) -> Bool {
        let path = url.standardizedFileURL.path
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        if path.hasPrefix("/Applications/") || path.hasPrefix(home + "/Applications/") { return false }
        if path.contains("/DerivedData/") || path.contains("/Netlane/build/") || path.contains("/Netlane/dist/") { return false } // dev builds
        return isOnReadOnlyVolume(url)
            || isTranslocated(Bundle.main.bundleURL)
            || path.hasPrefix("/Volumes/")
            || path.hasPrefix(home + "/Downloads/")
    }

    private static func isOnReadOnlyVolume(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly) == true
    }

    private static func volumeURL(of url: URL) -> URL? {
        guard let vol = try? url.resourceValues(forKeys: [.volumeURLKey]).volume,
              vol.path.hasPrefix("/Volumes/") else { return nil }
        return vol
    }

    // Gatekeeper may run a quarantined app from a random read-only path (App Translocation).
    // Resolve back to where the user actually launched it from.
    private typealias IsTranslocatedFn = @convention(c) (CFURL, UnsafeMutablePointer<Bool>, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Bool
    private typealias OriginalPathFn = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?

    private static let security = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY)

    private static func isTranslocated(_ url: URL) -> Bool {
        guard let sym = dlsym(security, "SecTranslocateIsTranslocatedURL") else {
            return url.path.contains("/AppTranslocation/")
        }
        var result = false
        _ = unsafeBitCast(sym, to: IsTranslocatedFn.self)(url as CFURL, &result, nil)
        return result
    }

    private static func originalBundleURL() -> URL {
        let url = Bundle.main.bundleURL
        guard isTranslocated(url),
              let sym = dlsym(security, "SecTranslocateCreateOriginalPathForURL"),
              let original = unsafeBitCast(sym, to: OriginalPathFn.self)(url as CFURL, nil)?.takeRetainedValue()
        else { return url }
        return original as URL
    }

    // MARK: - Install

    static func install(from source: URL, to destination: URL) throws {
        let fm = FileManager.default
        do {
            if fm.fileExists(atPath: destination.path) {
                try fm.trashItem(at: destination, resultingItemURL: nil)
            }
            try fm.copyItem(at: source, to: destination)
        } catch {
            // Non-admin accounts can't write to /Applications directly; ask for credentials.
            try installWithAdminRights(from: source, to: destination)
        }
        // The user already chose to open this copy; don't make Gatekeeper ask again for the installed one.
        removeQuarantine(destination)

        // A copy left in Downloads etc. would be confusing; a read-only disk image can't be modified anyway.
        if !isOnReadOnlyVolume(source) {
            try? fm.trashItem(at: source, resultingItemURL: nil)
        }
    }

    private static func installWithAdminRights(from source: URL, to destination: URL) throws {
        func q(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let shell = "/bin/rm -rf \(q(destination.path)) && /usr/bin/ditto \(q(source.path)) \(q(destination.path))"
        let script = "do shell script \"\(shell.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\""))\" with administrator privileges"
        var errorInfo: NSDictionary?
        NSAppleScript(source: script)?.executeAndReturnError(&errorInfo)
        if let errorInfo {
            let message = errorInfo[NSAppleScript.errorMessage] as? String ?? "Authorization failed."
            throw NSError(domain: "Netlane", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
        }
    }

    private static func removeQuarantine(_ url: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        p.arguments = ["-dr", "com.apple.quarantine", url.path]
        try? p.run()
        p.waitUntilExit()
    }

    // MARK: - Relaunch

    /// Waits for this process to exit, opens the installed copy, then ejects the disk image we came from.
    static func relaunch(_ app: URL, ejecting volume: URL?) {
        let pid = ProcessInfo.processInfo.processIdentifier
        var script = "while /bin/kill -0 \(pid) 2>/dev/null; do /bin/sleep 0.2; done; /usr/bin/open \"$1\""
        var args = ["-c", "", "netlane-relaunch", app.path]
        if let volume {
            script += "; /bin/sleep 1; /usr/bin/hdiutil detach -quiet \"$2\" || /usr/bin/hdiutil detach -quiet -force \"$2\""
            args.append(volume.path)
        }
        args[1] = script
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = args
        try? p.run()
        NSApp.terminate(nil)
    }
}
