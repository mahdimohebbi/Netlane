import SwiftUI

struct SettingsView: View {
    let model: AppModel
    @Bindable var prefs: Preferences
    let updater: Updater

    var body: some View {
        Form {
            Section("General") {
                Toggle("Launch at Login", isOn: Binding(get: { model.launchAtLogin }, set: { model.setLaunchAtLogin($0) }))
                if let error = model.launchAtLoginError {
                    Text(error).font(.caption).foregroundStyle(.orange)
                }
                Toggle("Popup closes automatically when clicking outside", isOn: $prefs.popoverClosesAutomatically)
                Toggle("Show a toast when the IP address changes", isOn: $prefs.showIPChangeToast)
                Picker("Theme", selection: $prefs.theme) {
                    Text("System").tag(Preferences.Theme.system)
                    Text("Light").tag(Preferences.Theme.light)
                    Text("Dark").tag(Preferences.Theme.dark)
                }
                .pickerStyle(.segmented)
                Picker("Unit (Bits or Bytes)", selection: $prefs.speedUnit) {
                    Text("Mb/s").tag(Preferences.SpeedUnit.bits)
                    Text("MB/s").tag(Preferences.SpeedUnit.bytes)
                }
                .pickerStyle(.radioGroup)
                .horizontalRadioGroupLayout()
            }

            Section("Updates") {
                Toggle("Automatically check for updates", isOn: $prefs.checkForUpdates)
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Version \(updater.currentVersion)")
                        Text(updateStatus).font(.caption).foregroundStyle(updateStatusIsError ? .orange : .secondary)
                    }
                    Spacer()
                    if let release = updater.latest, !updater.isBusy {
                        Button("Update to \(release.version)") { Task { await updater.install(release) } }
                    } else {
                        Button("Check for Updates") { Task { await updater.check(userInitiated: true) } }
                            .disabled(updater.isBusy)
                    }
                }
            }

            Section("Menu Bar") {
                Toggle("Show live upload & download speed", isOn: $prefs.showLiveSpeed)
                Group {
                    Picker("Bandwidth Data", selection: $prefs.bandwidthData) {
                        Text("Download & Upload").tag(Preferences.BandwidthData.both)
                        Text("Download only").tag(Preferences.BandwidthData.download)
                        Text("Upload only").tag(Preferences.BandwidthData.upload)
                    }
                    Picker("Button Style", selection: $prefs.buttonStyle) {
                        Text("Single line").tag(Preferences.ButtonStyle.singleLine)
                        Text("Double line").tag(Preferences.ButtonStyle.doubleLine)
                    }
                    .pickerStyle(.radioGroup)
                    .horizontalRadioGroupLayout()
                    Toggle("Show upload on top", isOn: $prefs.uploadOnTop)
                        .disabled(prefs.bandwidthData != .both || prefs.buttonStyle != .doubleLine)
                    Toggle("Enable custom menu bar font color", isOn: $prefs.customColors)
                    HStack {
                        ColorPicker("Download data color", selection: colorBinding(\.downloadColorHex), supportsOpacity: false)
                        Spacer(minLength: 20)
                        ColorPicker("Upload data color", selection: colorBinding(\.uploadColorHex), supportsOpacity: false)
                    }
                    .disabled(!prefs.customColors)
                }
                .disabled(!prefs.showLiveSpeed)
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var updateStatusIsError: Bool {
        if case .failed = updater.state { return true }
        return false
    }

    private var updateStatus: String {
        switch updater.state {
        case .idle: "Not checked yet"
        case .checking: "Checking…"
        case .upToDate: "Netlane is up to date"
        case .available(let release): "Version \(release.version) is available"
        case .downloading(let fraction): "Downloading… \(Int(fraction * 100))%"
        case .installing: "Installing…"
        case .failed(let message): message
        }
    }

    private func colorBinding(_ keyPath: ReferenceWritableKeyPath<Preferences, String>) -> Binding<Color> {
        Binding(
            get: { Color(nsColor: NSColor(hex: prefs[keyPath: keyPath]) ?? .white) },
            set: { prefs[keyPath: keyPath] = NSColor($0).hexString }
        )
    }
}
