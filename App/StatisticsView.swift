import AppKit
import Charts
import Observation
import SwiftUI

enum ByteFormat {
    static func string(_ bytes: UInt64) -> String {
        guard bytes > 0 else { return "0 B" }
        return ByteCountFormatter.string(fromByteCount: Int64(min(bytes, UInt64(Int64.max))), countStyle: .decimal)
    }
}

/// Which tab and range the statistics window shows. A class (rather than @State) so it builds with the Command Line Tools too.
@MainActor
@Observable
final class StatisticsSelection {
    enum Tab: String, CaseIterable, Identifiable {
        case live = "Live", history = "History"
        var id: String { rawValue }
    }

    var tab: Tab = .live
    var range: TrafficLog.Range = .week
}

/// Download & Upload Statistics: a live chart of the last minute, and a filterable history.
struct StatisticsView: View {
    let model: AppModel
    let prefs: Preferences

    @Bindable var selection: StatisticsSelection
    private var range: TrafficLog.Range { selection.range }

    private var stats: TrafficStats { model.stats }
    private static let downColor = Color.green
    private static let upColor = Color.blue

    var body: some View {
        VStack(spacing: 16) {
            Picker("", selection: $selection.tab) {
                ForEach(StatisticsSelection.Tab.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 220)

            switch selection.tab {
            case .live: live
            case .history: history
            }
        }
        .padding(20)
        .frame(width: 600, height: 700, alignment: .top)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    // MARK: Live

    private var live: some View {
        VStack(spacing: 16) {
            HStack(spacing: 12) {
                tile("Download", color: Self.downColor, value: prefs.formatRate(bytesPerSecond: model.downloadRate),
                     caption: "Peak \(prefs.formatRate(bytesPerSecond: stats.peakDown)) this session")
                tile("Upload", color: Self.upColor, value: prefs.formatRate(bytesPerSecond: model.uploadRate),
                     caption: "Peak \(prefs.formatRate(bytesPerSecond: stats.peakUp)) this session")
            }

            card {
                HStack {
                    Text("Last 60 seconds").font(.headline)
                    Spacer()
                    HStack(spacing: 5) {
                        Circle().fill(Self.downColor).frame(width: 7, height: 7)
                        Text("Live")
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
                liveChart.frame(height: 190)
                HStack {
                    Text("60 s ago"); Spacer(); Text("30 s"); Spacer(); Text("now")
                }
                .font(.caption).foregroundStyle(.secondary)
            }

            HStack(spacing: 12) {
                totalTile("This session", stats.sessionRx, stats.sessionTx)
                totalTile("Today", stats.totalToday().rx, stats.totalToday().tx)
                totalTile("This month", stats.totalThisMonth().rx, stats.totalThisMonth().tx)
            }
            Spacer(minLength: 0)
            Text("Counted while Netlane is running. Stored only on this Mac.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var rateUnit: String { prefs.speedUnit == .bits ? "Mb/s" : "MB/s" }

    private func scaled(_ bytesPerSecond: Double) -> Double {
        prefs.speedUnit == .bits ? bytesPerSecond * 8 / 1_000_000 : bytesPerSecond / 1_000_000
    }

    private var liveChart: some View {
        let samples = stats.live
        let last = samples.last?.id ?? TrafficStats.liveWindow
        return Chart {
            ForEach(samples) { s in
                AreaMark(x: .value("Time", s.id), y: .value(rateUnit, scaled(s.down)), series: .value("Series", "Download"), stacking: .unstacked)
                    .foregroundStyle(Self.downColor.opacity(0.15))
                LineMark(x: .value("Time", s.id), y: .value(rateUnit, scaled(s.down)), series: .value("Series", "Download"))
                    .foregroundStyle(Self.downColor).interpolationMethod(.monotone)
                AreaMark(x: .value("Time", s.id), y: .value(rateUnit, scaled(s.up)), series: .value("Series", "Upload"), stacking: .unstacked)
                    .foregroundStyle(Self.upColor.opacity(0.15))
                LineMark(x: .value("Time", s.id), y: .value(rateUnit, scaled(s.up)), series: .value("Series", "Upload"))
                    .foregroundStyle(Self.upColor).interpolationMethod(.monotone)
            }
        }
        .chartXScale(domain: (last - TrafficStats.liveWindow + 1)...last)
        .chartXAxis(.hidden)
        .chartYAxisLabel(rateUnit, alignment: .trailing)
        .chartYScale(domain: .automatic(includesZero: true))
        .accessibilityLabel("Download and upload speed over the last 60 seconds")
    }

    // MARK: History

    private var history: some View {
        let buckets = stats.log.buckets(range)
        let down = buckets.reduce(UInt64(0)) { $0 + $1.rx }
        let up = buckets.reduce(UInt64(0)) { $0 + $1.tx }
        let busiest = buckets.max { $0.total < $1.total }
        return VStack(spacing: 16) {
            HStack {
                Picker("", selection: $selection.range) {
                    ForEach(TrafficLog.Range.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 200)
                Spacer()
                Text(rangeTitle).font(.callout).foregroundStyle(.secondary)
            }

            HStack(spacing: 12) {
                summary("Downloaded", ByteFormat.string(down), color: Self.downColor)
                summary("Uploaded", ByteFormat.string(up), color: Self.upColor)
                if range == .day {
                    summary("Busiest hour", busiest.map { $0.total > 0 ? "\(hourLabel($0.start)) · \(ByteFormat.string($0.total))" : "—" } ?? "—", color: .secondary)
                } else {
                    summary("Daily average", ByteFormat.string((down + up) / UInt64(max(buckets.count, 1))), color: .secondary)
                }
            }

            card { historyChart(buckets).frame(height: 170) }

            card(padding: 0) {
                HStack {
                    Text(range == .day ? "Hour" : "Day").frame(maxWidth: .infinity, alignment: .leading)
                    Text("Download").frame(width: 100, alignment: .trailing)
                    Text("Upload").frame(width: 100, alignment: .trailing)
                    Text("Total").frame(width: 100, alignment: .trailing)
                }
                .font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                .padding(.horizontal, 14).padding(.vertical, 8)
                Divider()
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(buckets.reversed()) { b in
                            HStack {
                                Text(rowLabel(b.start)).frame(maxWidth: .infinity, alignment: .leading)
                                Text(ByteFormat.string(b.rx)).foregroundStyle(.green).frame(width: 100, alignment: .trailing)
                                Text(ByteFormat.string(b.tx)).foregroundStyle(.blue).frame(width: 100, alignment: .trailing)
                                Text(ByteFormat.string(b.total)).fontWeight(.semibold).frame(width: 100, alignment: .trailing)
                            }
                            .font(.callout.monospacedDigit())
                            .padding(.horizontal, 14).padding(.vertical, 6)
                            Divider()
                        }
                    }
                }
            }

            HStack {
                Text("Stored only on this Mac · kept for 12 months").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Export CSV…", action: exportCSV)
                Button("Clear History…", role: .destructive, action: confirmClear)
            }
            .controlSize(.small)
        }
    }

    private func historyChart(_ buckets: [TrafficLog.Bucket]) -> some View {
        let unit: Calendar.Component = range == .day ? .hour : .day
        return Chart {
            ForEach(buckets) { b in
                BarMark(x: .value("Period", b.start, unit: unit), y: .value("Bytes", Double(b.rx)))
                    .foregroundStyle(by: .value("Type", "Download")).position(by: .value("Type", "Download"))
                BarMark(x: .value("Period", b.start, unit: unit), y: .value("Bytes", Double(b.tx)))
                    .foregroundStyle(by: .value("Type", "Upload")).position(by: .value("Type", "Upload"))
            }
        }
        .chartForegroundStyleScale(["Download": Self.downColor, "Upload": Self.upColor])
        .chartLegend(.hidden)
        .chartYAxis {
            AxisMarks { value in
                AxisGridLine()
                AxisValueLabel { if let v = value.as(Double.self) { Text(ByteFormat.string(UInt64(max(v, 0)))) } }
            }
        }
        .chartXAxis {
            switch range {
            case .day:
                AxisMarks(values: .stride(by: .hour, count: 6)) { _ in AxisValueLabel(format: .dateTime.hour(.twoDigits(amPM: .omitted))) }
            case .week:
                AxisMarks(values: .stride(by: .day)) { _ in AxisValueLabel(format: .dateTime.weekday(.abbreviated), centered: true) }
            case .month:
                AxisMarks(values: .stride(by: .day, count: 5)) { _ in AxisValueLabel(format: .dateTime.day(), centered: true) }
            }
        }
        .accessibilityLabel("Download and upload per \(range == .day ? "hour" : "day")")
    }

    private var rangeTitle: String {
        switch range {
        case .day: "Today, \(Date().formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated)))"
        case .week: "Last 7 days"
        case .month: "Last 30 days"
        }
    }

    private func hourLabel(_ date: Date) -> String { date.formatted(.dateTime.hour(.twoDigits(amPM: .omitted))) + ":00" }

    private func rowLabel(_ date: Date) -> String {
        range == .day ? hourLabel(date) : date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
    }

    private func exportCSV() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "Netlane-traffic.csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try stats.log.csv().write(to: url, atomically: true, encoding: .utf8) }
        catch { NSAlert(error: error).runModal() }
    }

    private func confirmClear() {
        let alert = NSAlert()
        alert.messageText = "Clear all traffic history?"
        alert.informativeText = "This permanently deletes the recorded download and upload totals on this Mac."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Clear History")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn { stats.clearHistory() }
    }

    // MARK: Pieces

    private func card<Content: View>(padding: CGFloat = 16, @ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8, content: content)
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.background, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator.opacity(0.6)))
    }

    private func tile(_ title: String, color: Color, value: String, caption: String) -> some View {
        card {
            Label(title, systemImage: "circle.fill").font(.caption.weight(.semibold)).foregroundStyle(color)
            Text(value).font(.system(size: 28, weight: .semibold).monospacedDigit())
            Text(caption).font(.caption).foregroundStyle(.secondary)
        }
    }

    private func summary(_ title: String, _ value: String, color: Color) -> some View {
        card {
            Text(title).font(.caption.weight(.semibold)).foregroundStyle(color)
            Text(value).font(.title3.weight(.semibold).monospacedDigit()).lineLimit(1).minimumScaleFactor(0.7)
        }
    }

    private func totalTile(_ title: String, _ rx: UInt64, _ tx: UInt64) -> some View {
        card {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Text("↓ \(ByteFormat.string(rx))").font(.callout.weight(.semibold).monospacedDigit())
            Text("↑ \(ByteFormat.string(tx))").font(.callout.weight(.semibold).monospacedDigit())
        }
    }
}

/// The popup's entry point: current speeds, today's totals and a mini trend line.
struct StatisticsCard: View {
    let model: AppModel
    let prefs: Preferences
    let open: () -> Void

    var body: some View {
        let today = model.stats.totalToday()
        VStack(alignment: .leading, spacing: 8) {
            Label("Statistics", systemImage: "chart.xyaxis.line").font(.callout.weight(.semibold))
            HStack(alignment: .bottom) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("↓ \(prefs.formatRate(bytesPerSecond: model.downloadRate))").foregroundStyle(.green).fontWeight(.semibold)
                    Text("↑ \(prefs.formatRate(bytesPerSecond: model.uploadRate))").foregroundStyle(.blue).fontWeight(.semibold)
                    Text("Today \(ByteFormat.string(today.rx)) ↓ · \(ByteFormat.string(today.tx)) ↑")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .font(.callout.monospacedDigit())
                Spacer()
                miniChart
                    .frame(width: 130, height: 44)
            }
            Button("Open Statistics", action: open).controlSize(.small)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var miniChart: some View {
        let samples = model.stats.live
        let last = samples.last?.id ?? TrafficStats.liveWindow
        return Chart {
            ForEach(samples) { s in
                AreaMark(x: .value("t", s.id), y: .value("Down", s.down), series: .value("s", "d"), stacking: .unstacked)
                    .foregroundStyle(Color.green.opacity(0.18))
                LineMark(x: .value("t", s.id), y: .value("Down", s.down), series: .value("s", "d"))
                    .foregroundStyle(Color.green).interpolationMethod(.monotone)
                AreaMark(x: .value("t", s.id), y: .value("Up", s.up), series: .value("s", "u"), stacking: .unstacked)
                    .foregroundStyle(Color.blue.opacity(0.18))
                LineMark(x: .value("t", s.id), y: .value("Up", s.up), series: .value("s", "u"))
                    .foregroundStyle(Color.blue).interpolationMethod(.monotone)
            }
        }
        .chartXScale(domain: (last - TrafficStats.liveWindow + 1)...last)
        .chartYScale(domain: .automatic(includesZero: true))
        .chartXAxis(.hidden).chartYAxis(.hidden)
        .accessibilityHidden(true)
    }
}
