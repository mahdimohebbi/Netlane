import AppKit

enum SelfTest {
    static let ipwhoisSample = """
    {"ip":"203.0.113.42","success":true,"type":"IPv4","continent":"Europe","country":"Germany",\
    "country_code":"DE","region":"Berlin","city":"Berlin","connection":{"asn":3320,\
    "org":"Deutsche Telekom AG","isp":"Deutsche Telekom AG","domain":"telekom.de"},\
    "timezone":{"id":"Europe/Berlin","abbr":"CET","utc":"+01:00"}}
    """

    static let ipapiSample = """
    {"ip":"198.51.100.7","city":"Istanbul","region":"Istanbul","country":"TR","country_name":"Turkey",\
    "country_code":"TR","timezone":"Europe/Istanbul","asn":"AS9121","org":"Turk Telekom"}
    """

    private static var failures = 0

    private static func check(_ condition: Bool, _ name: String) {
        print(condition ? "PASS  \(name)" : "FAIL  \(name)")
        if !condition { failures += 1 }
    }

    static func run() -> Bool {
        check(Flag.emoji(for: "US") == "🇺🇸", "flag US")
        check(Flag.emoji(for: "DE") == "🇩🇪", "flag DE")
        check(Flag.emoji(for: "IR") == "🇮🇷", "flag IR")
        check(Flag.emoji(for: "tr") == "🇹🇷", "flag TR (lowercase)")
        check(Flag.emoji(for: "X1") == Flag.loading, "flag invalid code")

        let a = try? GeoClient.parseIpwhois(Data(ipwhoisSample.utf8))
        check(a?.ip == "203.0.113.42" && a?.countryCode == "DE" && a?.country == "Germany"
              && a?.city == "Berlin" && a?.isp == "Deutsche Telekom AG" && a?.asn == "AS3320"
              && a?.timezone == "Europe/Berlin", "parse ipwho.is")
        check((try? GeoClient.parseIpwhois(Data(#"{"success":false,"message":"Reserved range"}"#.utf8))) == nil,
              "ipwho.is success=false rejected")

        let b = try? GeoClient.parseIpapi(Data(ipapiSample.utf8))
        check(b?.ip == "198.51.100.7" && b?.countryCode == "TR" && b?.country == "Turkey"
              && b?.city == "Istanbul" && b?.isp == "Turk Telekom" && b?.asn == "AS9121"
              && b?.timezone == "Europe/Istanbul", "parse ipapi.co")

        check(abs(Throughput.mbps(bytes: 25_000_000, seconds: 2) - 100) < 0.0001, "Mbps 25MB/2s = 100")
        check(abs(Throughput.mbps(bytes: 1_250_000, seconds: 1) - 10) < 0.0001, "Mbps 1.25MB/1s = 10")
        check(Throughput.mbps(bytes: 100, seconds: 0) == 0, "Mbps zero duration")

        check(Preferences.formatRate(bytesPerSecond: 1_250_000, unit: .bits) == "10.0 Mb/s", "rate 1.25MB/s = 10.0 Mb/s")
        check(Preferences.formatRate(bytesPerSecond: 1_260_000, unit: .bytes) == "1.3 MB/s", "rate 1.26MB/s = 1.3 MB/s")
        check(Preferences.formatRate(bytesPerSecond: 512, unit: .bytes) == "512 B/s", "rate 512 B/s")
        check(Preferences.formatRate(bytesPerSecond: 250_000_000, unit: .bits) == "2.0 Gb/s", "rate 2 Gb/s")
        check(NSColor(hex: "#34C759")?.hexString == "#34C759", "color hex round trip")

        check(Updater.isNewer("1.3", than: "1.2"), "update: 1.3 > 1.2")
        check(Updater.isNewer("1.10", than: "1.9"), "update: 1.10 > 1.9 (numeric)")
        check(Updater.isNewer("v2.0.0", than: "1.9.9"), "update: tag prefix v")
        check(!Updater.isNewer("1.2.0", than: "1.2"), "update: 1.2.0 == 1.2")
        check(!Updater.isNewer("1.1", than: "1.2"), "update: older is not newer")
        check(!Updater.isNewer("", than: "1.2"), "update: empty version")
        let releaseJSON = """
        {"tag_name":"v1.3","html_url":"https://github.com/mahdimohebbi/Netlane/releases/tag/v1.3","body":"## What's new\\n- **Faster**\\n\\n## Downloads\\n| x |",\
        "assets":[{"name":"Netlane-win-x64.zip","browser_download_url":"https://github.com/x/win.zip","digest":"sha256:AA"},\
        {"name":"Netlane.dmg","browser_download_url":"https://github.com/x/Netlane.dmg","digest":"sha256:ABCDEF"}]}
        """
        let release = try? Updater.parseRelease(Data(releaseJSON.utf8))
        check(release?.version == "1.3" && release?.dmgURL.lastPathComponent == "Netlane.dmg"
              && release?.sha256 == "abcdef", "update: parse release picks the DMG and its digest")
        check(Updater.summary(of: release?.notes ?? "") == "Faster", "update: release notes summary")
        check((try? Updater.parseRelease(Data(#"{"tag_name":"v9","html_url":"https://x.y","assets":[]}"#.utf8))) == nil,
              "update: release without a DMG is ignored")

        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        func at(_ d: Int, _ h: Int, _ m: Int = 30) -> Date {
            cal.date(from: DateComponents(year: 2026, month: 11, day: d, hour: h, minute: m))!
        }
        let now = at(15, 21)
        var log = TrafficLog()
        log.add(rx: 100, tx: 10, at: at(15, 21, 5), calendar: cal)
        log.add(rx: 50, tx: 5, at: at(15, 21, 55), calendar: cal)   // same hour: merged
        log.add(rx: 7, tx: 1, at: at(15, 3), calendar: cal)         // earlier hour, inserted before
        log.add(rx: 1000, tx: 100, at: at(14, 12), calendar: cal)   // yesterday
        log.add(rx: 0, tx: 0, at: at(15, 22), calendar: cal)        // nothing to record
        check(log.entries.count == 3 && log.entries == log.entries.sorted { $0.t < $1.t }, "traffic: hours merge and stay sorted")
        let day = log.buckets(.day, now: now, calendar: cal)
        check(day.count == 24 && day[21].rx == 150 && day[21].tx == 15 && day[3].rx == 7 && day[0].total == 0, "traffic: day = 24 hourly buckets")
        let week = log.buckets(.week, now: now, calendar: cal)
        check(week.count == 7 && week[6].rx == 157 && week[5].rx == 1000 && week[0].total == 0, "traffic: week = 7 daily buckets, today last")
        check(log.buckets(.month, now: now, calendar: cal).count == 30, "traffic: month = 30 daily buckets")
        check(log.total(from: at(15, 0, 0), to: at(16, 0, 0)).rx == 157, "traffic: total in range")
        check((try? JSONDecoder().decode(TrafficLog.self, from: JSONEncoder().encode(log))) == log, "traffic: log survives encode/decode")
        let csvLines = log.csv().split(separator: "\n")
        check(csvLines.count == 4 && csvLines[0] == "hour_start,downloaded_bytes,uploaded_bytes" && csvLines[3].hasSuffix(",150,15"), "traffic: csv export")
        var old = TrafficLog()
        old.add(rx: 5, tx: 5, at: cal.date(byAdding: .day, value: -400, to: now)!, calendar: cal)
        old.add(rx: 6, tx: 6, at: cal.date(byAdding: .day, value: -30, to: now)!, calendar: cal)
        old.prune(now: now, calendar: cal)
        check(old.entries.count == 1 && old.entries[0].rx == 6, "traffic: entries older than 12 months are pruned")

        let statsFile = FileManager.default.temporaryDirectory.appendingPathComponent("netlane-selftest-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: statsFile) }
        MainActor.assumeIsolated {
            let stats = TrafficStats(fileURL: statsFile)
            for _ in 0..<75 { stats.record(rx: 1_000_000, tx: 200_000, elapsed: 1) }
            check(stats.live.count == 60 && stats.live.last?.id == 75, "traffic: live window keeps the last 60 samples")
            check(stats.sessionRx == 75_000_000 && stats.sessionTx == 15_000_000, "traffic: session totals")
            check(abs(stats.peakDown - 1_000_000) < 1 && abs(stats.live[0].up - 200_000) < 1, "traffic: rates and peaks")
            stats.record(rx: 9_000_000_000, tx: 0, elapsed: 3600) // woke from sleep: counters can't be trusted
            check(stats.sessionRx == 75_000_000 && stats.log.entries.reduce(0, { $0 + $1.rx }) == 75_000_000, "traffic: long gaps are not counted")
            stats.save()
            let reloaded = TrafficStats(fileURL: statsFile)
            check(reloaded.log.entries.reduce(0, { $0 + $1.rx }) == 75_000_000, "traffic: history persists across launches")
            reloaded.clearHistory()
            check(TrafficStats(fileURL: statsFile).log.entries.isEmpty, "traffic: clear history")
        }
        check(ByteFormat.string(0) == "0 B" && ByteFormat.string(6_410_000_000).hasSuffix("GB"), "traffic: byte formatting")

        let fallback = GeoClient { url in
            if url == GeoClient.primaryURL { throw URLError(.timedOut) }
            return Data(ipapiSample.utf8)
        }
        let bothFail = GeoClient { _ in throw URLError(.notConnectedToInternet) }
        let semaphore = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var fallbackResult: GeoInfo?
        nonisolated(unsafe) var bothFailThrew = false
        Task.detached {
            fallbackResult = try? await fallback.lookup()
            do { _ = try await bothFail.lookup() } catch { bothFailThrew = true }
            semaphore.signal()
        }
        semaphore.wait()
        check(fallbackResult?.countryCode == "TR", "fallback to ipapi.co when primary fails")
        check(bothFailThrew, "error when all providers fail")

        print(failures == 0 ? "SELFTEST OK" : "SELFTEST FAILED (\(failures))")
        return failures == 0
    }
}
