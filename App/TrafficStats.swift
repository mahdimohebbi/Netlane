import Foundation
import Observation

/// Hourly download/upload totals, kept for 12 months. A plain value type so it can be tested without a UI.
struct TrafficLog: Codable, Equatable {
    struct Entry: Codable, Equatable {
        var t: Int // start of the local hour, seconds since 1970
        var rx: UInt64
        var tx: UInt64
    }

    enum Range: String, CaseIterable, Identifiable {
        case day = "Day", week = "Week", month = "Month"
        var id: String { rawValue }
    }

    struct Bucket: Identifiable, Equatable {
        let start: Date
        let rx: UInt64
        let tx: UInt64
        var id: Date { start }
        var total: UInt64 { rx + tx }
    }

    static let retentionDays = 366

    var entries: [Entry] = []

    mutating func add(rx: UInt64, tx: UInt64, at date: Date, calendar: Calendar = .current) {
        guard rx > 0 || tx > 0, let hour = calendar.dateInterval(of: .hour, for: date) else { return }
        let start = Int(hour.start.timeIntervalSince1970)
        // Almost always the newest entry; search from the end.
        if let i = entries.lastIndex(where: { $0.t <= start }) {
            if entries[i].t == start {
                entries[i].rx &+= rx
                entries[i].tx &+= tx
            } else {
                entries.insert(Entry(t: start, rx: rx, tx: tx), at: i + 1)
            }
        } else {
            entries.insert(Entry(t: start, rx: rx, tx: tx), at: 0)
        }
    }

    mutating func prune(now: Date = Date(), calendar: Calendar = .current) {
        guard let cutoff = calendar.date(byAdding: .day, value: -Self.retentionDays, to: now) else { return }
        let t = Int(cutoff.timeIntervalSince1970)
        entries.removeAll { $0.t < t }
    }

    /// Bytes recorded in [from, to).
    func total(from: Date, to: Date) -> (rx: UInt64, tx: UInt64) {
        let a = Int(from.timeIntervalSince1970), b = Int(to.timeIntervalSince1970)
        var rx: UInt64 = 0, tx: UInt64 = 0
        for e in entries where e.t >= a && e.t < b {
            rx += e.rx
            tx += e.tx
        }
        return (rx, tx)
    }

    /// Day: each hour of today. Week: the last 7 days. Month: the last 30 days. Oldest first.
    func buckets(_ range: Range, now: Date = Date(), calendar: Calendar = .current) -> [Bucket] {
        let today = calendar.startOfDay(for: now)
        func make(_ start: Date, _ end: Date) -> Bucket {
            let sum = total(from: start, to: end)
            return Bucket(start: start, rx: sum.rx, tx: sum.tx)
        }
        switch range {
        case .day:
            guard let tomorrow = calendar.date(byAdding: .day, value: 1, to: today) else { return [] }
            var result: [Bucket] = []
            var start = today
            while start < tomorrow, let next = calendar.date(byAdding: .hour, value: 1, to: start) {
                result.append(make(start, next))
                start = next
            }
            return result
        case .week, .month:
            let days = range == .week ? 7 : 30
            return (0..<days).reversed().compactMap { back in
                guard let start = calendar.date(byAdding: .day, value: -back, to: today),
                      let end = calendar.date(byAdding: .day, value: 1, to: start) else { return nil }
                return make(start, end)
            }
        }
    }

    /// Everything recorded, one row per hour.
    func csv() -> String {
        let formatter = ISO8601DateFormatter()
        var lines = ["hour_start,downloaded_bytes,uploaded_bytes"]
        for e in entries {
            lines.append("\(formatter.string(from: Date(timeIntervalSince1970: TimeInterval(e.t)))),\(e.rx),\(e.tx)")
        }
        return lines.joined(separator: "\n") + "\n"
    }
}

/// Live speed history for the chart plus the persisted byte log. Samples arrive once a second from AppModel.
@MainActor
@Observable
final class TrafficStats {
    struct Sample: Identifiable, Equatable {
        let id: Int
        let down: Double // bytes per second
        let up: Double
    }

    static let liveWindow = 60
    /// A gap this long (sleep, stall) makes the byte counters unreliable, so those bytes are not added to history.
    private static let maxTrustedGap: TimeInterval = 10

    private(set) var live: [Sample] = []
    private(set) var log: TrafficLog
    private(set) var sessionRx: UInt64 = 0
    private(set) var sessionTx: UInt64 = 0
    private(set) var peakDown: Double = 0
    private(set) var peakUp: Double = 0

    private let fileURL: URL
    private var counter = 0
    private var lastSave = Date()
    private var dirty = false

    init(fileURL: URL = TrafficStats.defaultURL) {
        self.fileURL = fileURL
        if let data = try? Data(contentsOf: fileURL), let decoded = try? JSONDecoder().decode(TrafficLog.self, from: data) {
            log = decoded
        } else {
            log = TrafficLog()
        }
        log.prune()
    }

    nonisolated static var defaultURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("Netlane", isDirectory: true).appendingPathComponent("traffic.json")
    }

    func record(rx: UInt64, tx: UInt64, elapsed: TimeInterval, at date: Date = Date()) {
        guard elapsed > 0 else { return }
        let down = Double(rx) / elapsed, up = Double(tx) / elapsed
        counter += 1
        live.append(Sample(id: counter, down: down, up: up))
        if live.count > Self.liveWindow { live.removeFirst(live.count - Self.liveWindow) }
        peakDown = max(peakDown, down)
        peakUp = max(peakUp, up)

        guard elapsed <= Self.maxTrustedGap else { return }
        sessionRx += rx
        sessionTx += tx
        guard rx > 0 || tx > 0 else { return }
        log.add(rx: rx, tx: tx, at: date)
        dirty = true
        if date.timeIntervalSince(lastSave) >= 60 { save() }
    }

    func save() {
        guard dirty else { return }
        dirty = false
        lastSave = Date()
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(log).write(to: fileURL, options: .atomic)
        } catch {
            dirty = true // try again at the next save
        }
    }

    func clearHistory() {
        log = TrafficLog()
        dirty = true
        save()
    }

    // MARK: Totals shown in the UI

    func totalToday(now: Date = Date()) -> (rx: UInt64, tx: UInt64) {
        let start = Calendar.current.startOfDay(for: now)
        return log.total(from: start, to: now.addingTimeInterval(1))
    }

    func totalThisMonth(now: Date = Date()) -> (rx: UInt64, tx: UInt64) {
        guard let month = Calendar.current.dateInterval(of: .month, for: now) else { return (0, 0) }
        return log.total(from: month.start, to: month.end)
    }
}
