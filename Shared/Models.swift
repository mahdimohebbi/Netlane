import Foundation

struct GeoInfo: Codable, Equatable, Sendable {
    var ip: String
    var country: String
    var countryCode: String
    var city: String
    var isp: String
    var asn: String
    var timezone: String
    var fetchedAt: Date = Date()

    var flag: String { Flag.emoji(for: countryCode) }

    static let sample = GeoInfo(
        ip: "203.0.113.42", country: "Germany", countryCode: "DE", city: "Berlin",
        isp: "Deutsche Telekom AG", asn: "AS3320", timezone: "Europe/Berlin"
    )
}

enum Flag {
    static let loading = "🌐"
    static let offline = "⚠️"

    static func emoji(for countryCode: String) -> String {
        let code = countryCode.uppercased()
        guard code.count == 2 else { return loading }
        var result = ""
        for scalar in code.unicodeScalars {
            guard ("A"..."Z").contains(scalar),
                  let indicator = UnicodeScalar(0x1F1E6 + scalar.value - 65) else { return loading }
            result.unicodeScalars.append(indicator)
        }
        return result
    }
}

enum Throughput {
    static func mbps(bytes: Int64, seconds: Double) -> Double {
        guard seconds > 0, bytes > 0 else { return 0 }
        return Double(bytes) * 8 / seconds / 1_000_000
    }
}
