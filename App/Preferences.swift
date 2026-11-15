import AppKit
import Observation
import SwiftUI

@MainActor
@Observable
final class Preferences {
    enum Theme: String, CaseIterable { case system, light, dark }
    enum SpeedUnit: String, CaseIterable { case bits, bytes }
    enum ButtonStyle: String, CaseIterable { case singleLine, doubleLine }
    enum BandwidthData: String, CaseIterable { case both, download, upload }

    private let defaults = UserDefaults.standard

    var theme: Theme { didSet { save(theme.rawValue, "theme"); applyTheme() } }
    var speedUnit: SpeedUnit { didSet { save(speedUnit.rawValue, "speedUnit") } }
    var popoverClosesAutomatically: Bool { didSet { save(popoverClosesAutomatically, "popoverClosesAutomatically") } }
    var showIPChangeToast: Bool { didSet { save(showIPChangeToast, "showIPChangeToast") } }
    var showLiveSpeed: Bool { didSet { save(showLiveSpeed, "showLiveSpeed") } }
    var buttonStyle: ButtonStyle { didSet { save(buttonStyle.rawValue, "buttonStyle") } }
    var bandwidthData: BandwidthData { didSet { save(bandwidthData.rawValue, "bandwidthData") } }
    var uploadOnTop: Bool { didSet { save(uploadOnTop, "uploadOnTop") } }
    var checkForUpdates: Bool { didSet { save(checkForUpdates, "checkForUpdates") } }
    var skippedUpdateVersion: String { didSet { save(skippedUpdateVersion, "skippedUpdateVersion") } }
    var customColors: Bool { didSet { save(customColors, "customColors") } }
    var downloadColorHex: String { didSet { save(downloadColorHex, "downloadColorHex") } }
    var uploadColorHex: String { didSet { save(uploadColorHex, "uploadColorHex") } }

    init() {
        let d = UserDefaults.standard
        theme = Theme(rawValue: d.string(forKey: "theme") ?? "") ?? .system
        speedUnit = SpeedUnit(rawValue: d.string(forKey: "speedUnit") ?? "") ?? .bits
        popoverClosesAutomatically = d.object(forKey: "popoverClosesAutomatically") as? Bool ?? true
        showIPChangeToast = d.object(forKey: "showIPChangeToast") as? Bool ?? true
        showLiveSpeed = d.object(forKey: "showLiveSpeed") as? Bool ?? true
        buttonStyle = ButtonStyle(rawValue: d.string(forKey: "buttonStyle") ?? "") ?? .doubleLine
        bandwidthData = BandwidthData(rawValue: d.string(forKey: "bandwidthData") ?? "") ?? .both
        uploadOnTop = d.object(forKey: "uploadOnTop") as? Bool ?? false
        checkForUpdates = d.object(forKey: "checkForUpdates") as? Bool ?? true
        skippedUpdateVersion = d.string(forKey: "skippedUpdateVersion") ?? ""
        customColors = d.object(forKey: "customColors") as? Bool ?? false
        downloadColorHex = d.string(forKey: "downloadColorHex") ?? "#34C759"
        uploadColorHex = d.string(forKey: "uploadColorHex") ?? "#0A84FF"
    }

    private func save(_ value: Any, _ key: String) { defaults.set(value, forKey: key) }

    func applyTheme() {
        switch theme {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    var downloadColor: NSColor { NSColor(hex: downloadColorHex) ?? .systemGreen }
    var uploadColor: NSColor { NSColor(hex: uploadColorHex) ?? .systemBlue }

    func formatRate(bytesPerSecond: Double) -> String { Self.formatRate(bytesPerSecond: bytesPerSecond, unit: speedUnit) }

    func formatMbps(_ mbps: Double) -> String {
        speedUnit == .bits ? String(format: "%.1f Mbps", mbps) : String(format: "%.1f MB/s", mbps / 8)
    }

    nonisolated static func formatRate(bytesPerSecond: Double, unit: SpeedUnit) -> String {
        let (value, units) = unit == .bits
            ? (max(bytesPerSecond, 0) * 8, ["b/s", "Kb/s", "Mb/s", "Gb/s"])
            : (max(bytesPerSecond, 0), ["B/s", "KB/s", "MB/s", "GB/s"])
        var scaled = value
        var index = 0
        while scaled >= 1000, index < units.count - 1 {
            scaled /= 1000
            index += 1
        }
        let number = index == 0 || scaled >= 100 ? String(format: "%.0f", scaled) : String(format: "%.1f", scaled)
        return "\(number) \(units[index])"
    }
}

extension NSColor {
    convenience init?(hex: String) {
        let digits = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard digits.count == 6, let value = UInt32(digits, radix: 16) else { return nil }
        self.init(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                  blue: CGFloat(value & 0xFF) / 255, alpha: 1)
    }

    var hexString: String {
        guard let c = usingColorSpace(.sRGB) else { return "#FFFFFF" }
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)),
                      Int(round(c.greenComponent * 255)), Int(round(c.blueComponent * 255)))
    }
}
