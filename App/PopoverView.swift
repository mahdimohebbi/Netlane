import SwiftUI

struct PopoverView: View {
    let model: AppModel
    private var copied: Bool { model.ipCopied }

    var body: some View {
        VStack(spacing: 12) {
            header
            networkCard
            speedCard
            footer
        }
        .padding(14)
        .frame(width: 320)
        .background(.regularMaterial)
    }

    private var geo: GeoInfo? {
        if case .loaded(let geo) = model.geoState { return geo }
        return model.lastGeo
    }

    private var header: some View {
        HStack(spacing: 12) {
            Text(model.menuBarLabel).font(.system(size: 44))
            VStack(alignment: .leading, spacing: 2) {
                Text(geo?.country ?? (model.isOnline ? "Locating…" : "Offline")).font(.title3.weight(.semibold))
                Text(geo?.city.isEmpty == false ? geo!.city : "—").font(.subheadline).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                Task { await model.refresh() }
            } label: {
                if model.isRefreshing { ProgressView().controlSize(.small) }
                else { Image(systemName: "arrow.clockwise") }
            }
            .buttonStyle(.borderless)
            .frame(width: 24, height: 24)
            .help("Refresh")
            .disabled(model.isRefreshing)
        }
    }

    private var networkCard: some View {
        Card(title: "Network", symbol: "network") {
            if case .failed(let message) = model.geoState {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange).lineLimit(3)
            }
            Row("Public IP") {
                Button {
                    guard let ip = geo?.ip else { return }
                    model.copyPublicIP(ip)
                } label: {
                    HStack(spacing: 4) {
                        Text(copied ? "Copied" : (geo?.ip ?? "—")).font(.body.monospaced())
                        Image(systemName: copied ? "checkmark" : "doc.on.doc").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                .buttonStyle(.plain)
                .help("Copy public IP")
            }
            Row("Local IP") { Text(model.localIP).font(.body.monospaced()) }
            Row("ISP") { Text([geo?.isp, geo?.asn].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: " · ").nonEmpty ?? "—") }
            Row("Connection") {
                HStack(spacing: 6) {
                    if model.vpnLikely {
                        Text("VPN likely").font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(.blue.opacity(0.18), in: Capsule()).foregroundStyle(.blue)
                    }
                    Text(model.connectionType)
                }
            }
            Row("DNS") { Text(model.dnsServers.prefix(3).joined(separator: "\n").nonEmpty ?? "—").font(.body.monospaced()) }
            Row("Timezone") { Text(geo?.timezone.nonEmpty ?? "—") }
        }
    }

    private var speedCard: some View {
        Card(title: "Speed", symbol: "speedometer") {
            Row("Latency") { Text(model.speedResult?.latencyMs.map { String(format: "%.0f ms", $0) } ?? "—") }
            Row("Download") { Text(model.speedResult?.downloadMbps.map { String(format: "%.1f Mbps", $0) } ?? "—") }
            Row("Upload") { Text(model.speedResult?.uploadMbps.map { String(format: "%.1f Mbps", $0) } ?? "—") }
            if let error = model.speedError {
                Label(error, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
            }
            HStack {
                if model.speedPhase != .idle {
                    ProgressView().controlSize(.small)
                    Text(model.speedPhase.rawValue).font(.caption).foregroundStyle(.secondary)
                } else if let date = model.speedResult?.date {
                    Text("Last run \(date.formatted(date: .omitted, time: .shortened))").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Run test") { Task { await model.runSpeedTest() } }
                    .controlSize(.small)
                    .disabled(model.speedPhase != .idle || !model.isOnline)
            }
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Toggle("Launch at login", isOn: Binding(get: { model.launchAtLogin }, set: { model.setLaunchAtLogin($0) }))
                    .toggleStyle(.switch).controlSize(.mini)
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }.controlSize(.small)
            }
            if let error = model.launchAtLoginError {
                Text(error).font(.caption2).foregroundStyle(.orange).lineLimit(2)
            }
        }
        .font(.callout)
    }
}

private struct Card<Content: View>: View {
    let title: String
    let symbol: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(title, systemImage: symbol).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            content
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background.opacity(0.6), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.separator.opacity(0.5)))
    }
}

private struct Row<Value: View>: View {
    let label: String
    @ViewBuilder let value: Value

    init(_ label: String, @ViewBuilder value: () -> Value) {
        self.label = label
        self.value = value()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label).foregroundStyle(.secondary)
            Spacer(minLength: 12)
            value.multilineTextAlignment(.trailing).lineLimit(3).textSelection(.enabled)
        }
        .font(.callout)
    }
}

private extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}
