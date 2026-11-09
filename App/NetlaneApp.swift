import AppKit

@main
enum Launcher {
    static func main() {
        if CommandLine.arguments.contains("--selftest") {
            exit(SelfTest.run() ? 0 : 1)
        }
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: StatusBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if InstallLocation.moveToApplicationsIfNeeded() { return }
        controller = StatusBarController(model: AppModel(), prefs: Preferences())
    }

    // The widget's tap opens netlane://open.
    func application(_ application: NSApplication, open urls: [URL]) {
        controller?.showPopover()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        controller?.showPopover()
        return false
    }
}
