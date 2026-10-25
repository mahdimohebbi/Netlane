import AppKit
import Observation
import SwiftUI

@MainActor
final class StatusBarController: NSObject, NSPopoverDelegate {
    private let model: AppModel
    private let prefs: Preferences
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let popover = NSPopover()
    private var toast: NSPanel?
    private var toastDismiss: DispatchWorkItem?
    private var settingsWindow: NSWindow?

    init(model: AppModel, prefs: Preferences) {
        self.model = model
        self.prefs = prefs
        super.init()

        let hosting = NSHostingController(rootView: PopoverView(model: model, prefs: prefs) { [weak self] in
            self?.showSettings()
        })
        hosting.sizingOptions = .preferredContentSize
        popover.contentViewController = hosting
        popover.delegate = self

        if let button = statusItem.button {
            button.target = self
            button.action = #selector(togglePopover)
            button.imagePosition = .imageRight
            button.imageHugsTitle = true
        }

        model.onIPChange = { [weak self] old, new in self?.showIPChangeToast(from: old, to: new) }
        prefs.applyTheme()
        observe()
    }

    /// Re-renders the menu bar item whenever any state it reads changes.
    private func observe() {
        withObservationTracking {
            updateStatusItem()
        } onChange: { [weak self] in
            DispatchQueue.main.async { self?.observe() }
        }
    }

    private func updateStatusItem() {
        guard let button = statusItem.button else { return }
        model.setTrafficMonitoring(prefs.showLiveSpeed)
        popover.behavior = prefs.popoverClosesAutomatically ? .transient : .applicationDefined
        button.title = model.menuBarLabel
        button.image = prefs.showLiveSpeed ? speedImage() : nil
        button.toolTip = model.lastGeo.map { "\($0.country) · \($0.ip)" } ?? "FlagPulse"
    }

    private func speedImage() -> NSImage {
        let down = (arrow: "↓", text: prefs.formatRate(bytesPerSecond: model.downloadRate), color: prefs.downloadColor)
        let up = (arrow: "↑", text: prefs.formatRate(bytesPerSecond: model.uploadRate), color: prefs.uploadColor)
        var rows: [(arrow: String, text: String, color: NSColor)]
        switch prefs.bandwidthData {
        case .both: rows = prefs.uploadOnTop ? [up, down] : [down, up]
        case .download: rows = [down]
        case .upload: rows = [up]
        }
        let custom = prefs.customColors
        let doubleLine = prefs.buttonStyle == .doubleLine && rows.count == 2
        let font = NSFont.monospacedDigitSystemFont(ofSize: doubleLine ? 9 : 12, weight: doubleLine ? .semibold : .regular)
        // Width is measured from a worst-case string so the item doesn't jitter as numbers change.
        let widest = ("↓ " + (prefs.speedUnit == .bits ? "888.8 Kb/s" : "888.8 KB/s") as NSString)
            .size(withAttributes: [.font: font]).width.rounded(.up)
        let lines = doubleLine ? rows.map { [$0] } : [rows]
        let gap: CGFloat = 8
        let width = doubleLine ? widest : widest * CGFloat(rows.count) + gap * CGFloat(rows.count - 1)
        let size = NSSize(width: width + 4, height: 22)

        let image = NSImage(size: size, flipped: false) { _ in
            let lineHeight = font.ascender - font.descender
            let totalHeight = lineHeight * CGFloat(lines.count)
            var y = (size.height + totalHeight) / 2 - lineHeight
            for line in lines {
                var x: CGFloat = 4
                for row in line {
                    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: custom ? row.color : NSColor.black]
                    ("\(row.arrow) \(row.text)" as NSString).draw(at: NSPoint(x: x, y: y), withAttributes: attrs)
                    x += widest + gap
                }
                y -= lineHeight
            }
            return true
        }
        image.isTemplate = !custom
        return image
    }

    @objc private func togglePopover() {
        if popover.isShown { popover.performClose(nil) } else { showPopover() }
    }

    func showPopover() {
        guard let button = statusItem.button else { return }
        dismissToast()
        NSApp.activate()
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
    }

    // MARK: Toast

    private func showIPChangeToast(from old: GeoInfo, to new: GeoInfo) {
        guard prefs.showIPChangeToast, !popover.isShown,
              let button = statusItem.button, let buttonWindow = button.window else { return }
        dismissToast(animated: false)

        let view = NSHostingView(rootView: ToastView(old: old, new: new) { [weak self] in self?.showPopover() })
        let size = view.fittingSize
        let anchor = buttonWindow.frame
        let screen = buttonWindow.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        let x = min(max(anchor.midX - size.width / 2, screen.minX + 8), screen.maxX - size.width - 8)
        let panel = NSPanel(contentRect: NSRect(x: x, y: anchor.minY - size.height - 6, width: size.width, height: size.height),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.contentView = view
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .transient, .ignoresCycle]
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.2; panel.animator().alphaValue = 1 }
        toast = panel

        let work = DispatchWorkItem { [weak self] in self?.dismissToast() }
        toastDismiss = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: work)
    }

    private func dismissToast(animated: Bool = true) {
        toastDismiss?.cancel()
        guard let panel = toast else { return }
        toast = nil
        guard animated else { panel.orderOut(nil); return }
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.25; panel.animator().alphaValue = 0 }) {
            panel.orderOut(nil)
        }
    }

    // MARK: Settings

    func showSettings() {
        popover.performClose(nil)
        if settingsWindow == nil {
            let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(model: model, prefs: prefs)))
            window.title = "FlagPulse Settings"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            settingsWindow = window
        }
        NSApp.activate()
        settingsWindow?.makeKeyAndOrderFront(nil)
    }
}

private struct ToastView: View {
    let old: GeoInfo
    let new: GeoInfo
    let onTap: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Text(new.flag).font(.system(size: 30))
            VStack(alignment: .leading, spacing: 2) {
                Text(old.countryCode == new.countryCode ? "IP address changed" : "Now in \(new.country)")
                    .font(.headline)
                Text("\(old.ip) → \(new.ip)").font(.caption.monospaced()).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                if old.countryCode != new.countryCode {
                    Text("was \(old.flag) \(old.country)").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(width: 280, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator.opacity(0.6)))
        .contentShape(Rectangle())
        .onTapGesture(perform: onTap)
    }
}
