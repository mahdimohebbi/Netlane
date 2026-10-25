import SwiftUI
import WidgetKit

struct GeoEntry: TimelineEntry {
    var date: Date
    var geo: GeoInfo?
    var isStale = false
}

struct GeoProvider: TimelineProvider {
    private static let cacheKey = "lastGeo"

    func placeholder(in context: Context) -> GeoEntry { GeoEntry(date: Date(), geo: .sample) }

    func getSnapshot(in context: Context, completion: @escaping (GeoEntry) -> Void) {
        completion(GeoEntry(date: Date(), geo: context.isPreview ? .sample : Self.cached() ?? .sample))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<GeoEntry>) -> Void) {
        Task {
            let now = Date()
            do {
                let geo = try await GeoClient().lookup()
                if let data = try? JSONEncoder().encode(geo) { UserDefaults.standard.set(data, forKey: Self.cacheKey) }
                completion(Timeline(entries: [GeoEntry(date: now, geo: geo)], policy: .after(now.addingTimeInterval(15 * 60))))
            } catch {
                let entry = GeoEntry(date: now, geo: Self.cached(), isStale: true)
                completion(Timeline(entries: [entry], policy: .after(now.addingTimeInterval(5 * 60))))
            }
        }
    }

    private static func cached() -> GeoInfo? {
        guard let data = UserDefaults.standard.data(forKey: cacheKey) else { return nil }
        return try? JSONDecoder().decode(GeoInfo.self, from: data)
    }
}

struct NetlaneWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let entry: GeoEntry

    var body: some View {
        Group {
            if let geo = entry.geo {
                family == .systemMedium ? AnyView(medium(geo)) : AnyView(small(geo))
            } else {
                VStack(spacing: 6) {
                    Text(Flag.offline).font(.system(size: 36))
                    Text("Offline").font(.headline)
                    Text("Retrying soon").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .containerBackground(.fill.tertiary, for: .widget)
        .widgetURL(URL(string: "netlane://open"))
    }

    private func small(_ geo: GeoInfo) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .top) {
                Text(geo.flag).font(.system(size: 44))
                Spacer()
                if entry.isStale { Image(systemName: "wifi.slash").font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 0)
            Text(geo.country).font(.headline).lineLimit(1).minimumScaleFactor(0.7)
            Text(geo.ip).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private func medium(_ geo: GeoInfo) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text(geo.flag).font(.system(size: 48))
                Spacer(minLength: 0)
                Text(geo.country).font(.headline).lineLimit(1).minimumScaleFactor(0.7)
                Text(geo.city.isEmpty ? "—" : geo.city).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            VStack(alignment: .leading, spacing: 6) {
                info("Public IP", geo.ip, mono: true)
                info("ISP", geo.isp.isEmpty ? "—" : geo.isp)
                info("Timezone", geo.timezone.isEmpty ? "—" : geo.timezone)
                Spacer(minLength: 0)
                HStack(spacing: 4) {
                    if entry.isStale { Image(systemName: "wifi.slash") }
                    Text("Updated \(geo.fetchedAt.formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute(.twoDigits)))")
                }
                .font(.caption2).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        }
    }

    private func info(_ label: String, _ value: String, mono: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(label).font(.caption2).foregroundStyle(.secondary)
            Text(value).font(mono ? .caption.monospaced() : .caption).lineLimit(1).truncationMode(.middle)
        }
    }
}

@main
struct NetlaneWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "NetlaneWidget", provider: GeoProvider()) { entry in
            NetlaneWidgetView(entry: entry)
        }
        .configurationDisplayName("Netlane")
        .description("Your IP country and network at a glance.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}
