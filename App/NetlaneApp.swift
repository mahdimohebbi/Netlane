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
    private var updater: Updater?
    private var model: AppModel?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if InstallLocation.moveToApplicationsIfNeeded() { return }
        let prefs = Preferences()
        let updater = Updater(prefs: prefs)
        self.updater = updater
        let model = AppModel()
        self.model = model
        controller = StatusBarController(model: model, prefs: prefs, updater: updater)
        updater.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        model?.stats.save()
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
