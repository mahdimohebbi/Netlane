import SwiftUI

@main
enum Launcher {
    static func main() {
        if CommandLine.arguments.contains("--selftest") {
            exit(SelfTest.run() ? 0 : 1)
        }
        FlagPulseApp.main()
    }
}

struct FlagPulseApp: App {
    private let model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            PopoverView(model: model)
        } label: {
            Text(model.menuBarLabel)
        }
        .menuBarExtraStyle(.window)
    }
}
