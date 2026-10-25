import Foundation

struct SpeedResult: Sendable {
    var latencyMs: Double?
    var downloadMbps: Double?
    var uploadMbps: Double?
    var date = Date()
}

/// Counts bytes moved by a single task and stops it at a deadline, so a slow link still yields a measurement.
private final class TransferMeter: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var bytes: Int64 = 0
    private var start = Date()
    private var continuation: CheckedContinuation<(bytes: Int64, seconds: Double), Error>?

    func run(_ makeTask: (URLSession) -> URLSessionTask, cap: TimeInterval) async throws -> (bytes: Int64, seconds: Double) {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 8
        config.timeoutIntervalForResource = cap + 2
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        let task = makeTask(session)
        return try await withCheckedThrowingContinuation { cont in
            lock.withLock { continuation = cont; start = Date() }
            task.resume()
            DispatchQueue.global().asyncAfter(deadline: .now() + cap) { [weak self] in
                self?.finish(nil)
                task.cancel()
            }
        }
    }

    private func finish(_ error: Error?) {
        lock.lock()
        guard let cont = continuation else { lock.unlock(); return }
        continuation = nil
        let result = (bytes, Date().timeIntervalSince(start))
        lock.unlock()
        if let error, result.0 == 0 { cont.resume(throwing: error) } else { cont.resume(returning: result) }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        if dataTask.originalRequest?.httpMethod == "GET" { lock.withLock { bytes += Int64(data.count) } }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
        lock.withLock { bytes = totalBytesSent }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let http = task.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            finish(URLError(.badServerResponse))
        } else {
            finish(error)
        }
    }
}

enum SpeedTester {
    static let base = "https://speed.cloudflare.com"
    static let cap: TimeInterval = 8

    static func latency() async throws -> Double {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 8
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }
        let url = URL(string: "\(base)/__down?bytes=0")!
        _ = try? await session.data(from: url) // warm up DNS/TLS so samples reflect round trips
        var samples: [Double] = []
        for _ in 0..<3 {
            let start = Date()
            _ = try await session.data(from: url)
            samples.append(Date().timeIntervalSince(start) * 1000)
        }
        return samples.sorted()[1]
    }

    static func download() async throws -> Double {
        let url = URL(string: "\(base)/__down?bytes=25000000")!
        let r = try await TransferMeter().run({ $0.dataTask(with: url) }, cap: cap)
        return Throughput.mbps(bytes: r.bytes, seconds: r.seconds)
    }

    static func upload() async throws -> Double {
        var request = URLRequest(url: URL(string: "\(base)/__up")!)
        request.httpMethod = "POST"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        let body = Data(count: 10_000_000)
        let r = try await TransferMeter().run({ $0.uploadTask(with: request, from: body) }, cap: cap)
        return Throughput.mbps(bytes: r.bytes, seconds: r.seconds)
    }
}
