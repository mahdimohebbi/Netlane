import Foundation

enum GeoError: LocalizedError {
    case badResponse(String)
    case allProvidersFailed([String])

    var errorDescription: String? {
        switch self {
        case .badResponse(let message): return message
        case .allProvidersFailed(let messages): return "Lookup failed: " + messages.joined(separator: "; ")
        }
    }
}

struct GeoClient: Sendable {
    typealias Fetcher = @Sendable (URL) async throws -> Data

    static let primaryURL = URL(string: "https://ipwho.is/")!
    static let fallbackURL = URL(string: "https://ipapi.co/json/")!

    var fetch: Fetcher

    init(fetch: @escaping Fetcher = GeoClient.liveFetch) {
        self.fetch = fetch
    }

    static let liveFetch: Fetcher = { url in
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 8)
        request.setValue("Netlane/1.0", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw GeoError.badResponse("HTTP \(http.statusCode) from \(url.host ?? "server")")
        }
        return data
    }

    func lookup() async throws -> GeoInfo {
        var errors: [String] = []
        do {
            return try Self.parseIpwhois(try await fetch(Self.primaryURL))
        } catch {
            errors.append("ipwho.is: \(error.localizedDescription)")
        }
        do {
            return try Self.parseIpapi(try await fetch(Self.fallbackURL))
        } catch {
            errors.append("ipapi.co: \(error.localizedDescription)")
        }
        throw GeoError.allProvidersFailed(errors)
    }

    private struct IpwhoisPayload: Decodable {
        struct Connection: Decodable { var asn: Int?; var isp: String?; var org: String? }
        struct Timezone: Decodable { var id: String? }
        var success: Bool?
        var message: String?
        var ip: String?
        var country: String?
        var country_code: String?
        var city: String?
        var connection: Connection?
        var timezone: Timezone?
    }

    private struct IpapiPayload: Decodable {
        var ip: String?
        var city: String?
        var country_name: String?
        var country_code: String?
        var timezone: String?
        var asn: String?
        var org: String?
        var error: Bool?
        var reason: String?
    }

    static func parseIpwhois(_ data: Data) throws -> GeoInfo {
        let p = try JSONDecoder().decode(IpwhoisPayload.self, from: data)
        if p.success == false { throw GeoError.badResponse(p.message ?? "ipwho.is reported failure") }
        guard let ip = p.ip, let code = p.country_code else { throw GeoError.badResponse("Missing ip/country") }
        return GeoInfo(
            ip: ip, country: p.country ?? code, countryCode: code, city: p.city ?? "",
            isp: p.connection?.isp ?? p.connection?.org ?? "",
            asn: p.connection?.asn.map { "AS\($0)" } ?? "",
            timezone: p.timezone?.id ?? ""
        )
    }

    static func parseIpapi(_ data: Data) throws -> GeoInfo {
        let p = try JSONDecoder().decode(IpapiPayload.self, from: data)
        if p.error == true { throw GeoError.badResponse(p.reason ?? "ipapi.co reported failure") }
        guard let ip = p.ip, let code = p.country_code else { throw GeoError.badResponse("Missing ip/country") }
        return GeoInfo(
            ip: ip, country: p.country_name ?? code, countryCode: code, city: p.city ?? "",
            isp: p.org ?? "", asn: p.asn ?? "", timezone: p.timezone ?? ""
        )
    }
}
