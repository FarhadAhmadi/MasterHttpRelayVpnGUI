/**
 * MasterHttpRelay — Google Apps Script
 *
 * DEPLOYMENT:
 *   1. Go to https://script.google.com → New project
 *   2. Delete the default code, paste THIS entire file
 *   3. Click Deploy → New deployment
 *   4. Type: Web app  |  Execute as: Me  |  Who has access: Anyone
 *   5. Copy the Deployment ID into config.json as "script_id"
 *
 * CHANGE THE AUTH KEY BELOW TO YOUR OWN SECRET!
 */

const AUTH_KEY = "CHANGE_ME_TO_A_STRONG_SECRET";
const SIGNING_KEY = "CHANGE_ME_TO_ANOTHER_STRONG_SECRET";
const REQUIRE_SIGNED_REQUESTS = false; // Set true after Python client sends sig/ts/nonce/v.
const MAX_SKEW_SECONDS = 60;
const NONCE_TTL_SECONDS = 180;
const MAX_BODY_BYTES = 2 * 1024 * 1024;
const MAX_BATCH_ITEMS = 12;
const MAX_URL_LENGTH = 2048;
const ALLOW_HTTP = false;
const ALLOWED_METHODS = { GET: 1, POST: 1, PUT: 1, PATCH: 1, DELETE: 1, HEAD: 1, OPTIONS: 1 };
const BLOCK_PRIVATE_IP_TARGETS = true;
const RATE_LIMIT_WINDOW_SECONDS = 30;
const RATE_LIMIT_MAX_REQUESTS_PER_WINDOW = 180;
const RATE_LIMIT_MAX_BATCH_ITEMS_PER_WINDOW = 420;

// Relay is poor at solving bot challenges; keep these direct/no-relay in client routing.
const DENY_RELAY_HOST_SUFFIXES = [
  ".challenges.cloudflare.com",
  ".cloudflare.com",
  ".cloudflareinsights.com",
  ".hcaptcha.com",
  ".recaptcha.net",
  ".google.com",
];

// Keep browser capability headers (sec-ch-ua*, sec-fetch-*) intact.
// Some modern apps, notably Google Meet, use them for browser gating.
// Headers that reveal the user's real IP are also stripped here as a
// second line of defence (the Python client strips them first).
const SKIP_HEADERS = {
  host: 1, connection: 1, "content-length": 1,
  "transfer-encoding": 1, "proxy-connection": 1, "proxy-authorization": 1,
  "priority": 1, te: 1,
  // IP-leaking / proxy-metadata headers
  "x-forwarded-for": 1, "x-forwarded-host": 1, "x-forwarded-proto": 1,
  "x-forwarded-port": 1, "x-real-ip": 1, "forwarded": 1, "via": 1,
};

// If fetchAll fails, only retry methods that are safe to replay.
const SAFE_REPLAY_METHODS = { GET: 1, HEAD: 1, OPTIONS: 1 };

function doPost(e) {
  var startedMs = Date.now();
  var reqId = _newRequestId();
  _metricsTrackRequest();
  try {
    var req = JSON.parse(e.postData.contents);
    if (req.k !== AUTH_KEY) {
      _metricsTrackError("AUTH");
      return _jsonErr("unauthorized", "AUTH", reqId, startedMs, false);
    }
    var authErr = _verifyAuth(req);
    if (authErr) {
      _metricsTrackError("AUTH");
      return _jsonErr(authErr, "AUTH", reqId, startedMs, false);
    }
    var rl = _applyRateLimit(req);
    if (rl.blocked) {
      _metricsTrackError("RATE_LIMIT");
      return _jsonErr("rate limit exceeded", "RATE_LIMIT", reqId, startedMs, true);
    }

    // Batch mode: { k, q: [...] }
    if (Array.isArray(req.q)) return _doBatch(req.q, reqId, startedMs);

    // Single mode
    return _doSingle(req, reqId, startedMs);
  } catch (err) {
    _metricsTrackError("PARSE");
    return _jsonErr(String(err), "PARSE", reqId, startedMs, false);
  }
}

function _doSingle(req, reqId, startedMs) {
  var check = _validateTarget(req && req.u);
  if (!check.ok) {
    return _jsonErr(check.error, check.code, reqId, startedMs, check.retryable);
  }
  try {
    var opts = _buildOpts(req);
    var resp = UrlFetchApp.fetch(check.url, opts);
    _metricsTrackRequest(true);
    _metricsTrackLatency(Date.now() - startedMs);
    return _json(_okPayload(resp, reqId, startedMs));
  } catch (err) {
    _metricsTrackError("UPSTREAM");
    return _jsonErr(String(err), "UPSTREAM", reqId, startedMs, _isRetryableError(String(err)));
  }
}

function _doBatch(items, reqId, startedMs) {
  if (!Array.isArray(items) || items.length === 0) {
    return _jsonErr("empty batch", "BAD_BATCH", reqId, startedMs, false);
  }
  if (items.length > MAX_BATCH_ITEMS) {
    return _jsonErr("batch too large", "BAD_BATCH", reqId, startedMs, false);
  }
  var fetchArgs = [];
  var fetchIndex = [];
  var fetchMethods = [];
  var errorMap = {};

  for (var i = 0; i < items.length; i++) {
    var item = items[i];
    if (!item || typeof item !== "object") {
      errorMap[i] = _errObj("bad item", "BAD_ITEM", false);
      continue;
    }
    var check = _validateTarget(item.u);
    if (!check.ok) {
      errorMap[i] = _errObj(check.error, check.code, check.retryable);
      continue;
    }
    try {
      var opts = _buildOpts(item);
      opts.url = check.url;
      fetchArgs.push(opts);
      fetchIndex.push(i);
      fetchMethods.push(String(item.m || "GET").toUpperCase());
    } catch (err) {
      errorMap[i] = _errObj(String(err), "BUILD", false);
    }
  }

  // fetchAll() processes all requests in parallel inside Google
  var responses = [];
  if (fetchArgs.length > 0) {
    try {
      responses = UrlFetchApp.fetchAll(fetchArgs);
    } catch (err) {
      // If fetchAll fails as a whole, degrade to per-item fetch so one bad
      // request does not poison the full batch.
      responses = [];
      for (var j = 0; j < fetchArgs.length; j++) {
        try {
          if (!SAFE_REPLAY_METHODS[fetchMethods[j]]) {
            errorMap[fetchIndex[j]] = _errObj(
              "batch fetchAll failed; unsafe method not replayed",
              "BATCH_REPLAY_BLOCKED",
              false
            );
            responses[j] = null;
            continue;
          }
          var fallbackReq = fetchArgs[j];
          var fallbackUrl = fallbackReq.url;
          var fallbackOpts = {};
          for (var key in fallbackReq) {
            if (Object.prototype.hasOwnProperty.call(fallbackReq, key) && key !== "url") {
              fallbackOpts[key] = fallbackReq[key];
            }
          }
          responses[j] = UrlFetchApp.fetch(fallbackUrl, fallbackOpts);
        } catch (singleErr) {
          errorMap[fetchIndex[j]] = _errObj(String(singleErr), "UPSTREAM", _isRetryableError(String(singleErr)));
          responses[j] = null;
        }
      }
    }
  }

  var results = [];
  var rIdx = 0;
  for (var i = 0; i < items.length; i++) {
    if (Object.prototype.hasOwnProperty.call(errorMap, i)) {
      results.push(errorMap[i]);
    } else {
      var resp = responses[rIdx++];
      if (!resp) {
        results.push(_errObj("fetch failed", "UPSTREAM", true));
      } else {
        results.push(_okPayload(resp, reqId + ":" + i, startedMs));
      }
    }
  }
  return _json({ q: results, rid: reqId, t_ms: Date.now() - startedMs });
}

function _buildOpts(req) {
  var method = String(req.m || "GET").toUpperCase();
  if (!ALLOWED_METHODS[method]) throw new Error("method not allowed");
  var opts = {
    method: method.toLowerCase(),
    muteHttpExceptions: true,
    followRedirects: req.r !== false,
    validateHttpsCertificates: true,
    escaping: false,
  };
  if (req.h && typeof req.h === "object") {
    var headers = {};
    for (var k in req.h) {
      if (!req.h.hasOwnProperty(k)) continue;
      var lk = k.toLowerCase();
      if (SKIP_HEADERS[lk]) continue;
      var v = String(req.h[k]);
      if (v.length > 8192) continue;
      if (/[\r\n]/.test(v)) continue;
      headers[k] = v;
      }
    opts.headers = headers;
  }
  if (req.b) {
    var raw = Utilities.base64Decode(req.b);
    if (raw.length > MAX_BODY_BYTES) throw new Error("payload too large");
    opts.payload = raw;
    if (req.ct) opts.contentType = req.ct;
  }
  return opts;
}

function _respHeaders(resp) {
  try {
    if (typeof resp.getAllHeaders === "function") {
      return resp.getAllHeaders();
    }
  } catch (err) {}
  return resp.getHeaders();
}

function doGet(e) {
  if (e && e.parameter && (e.parameter.health === "1" || e.parameter.status === "1")) {
    return _json({
      ok: true,
      service: "masterrelay-apps-script",
      now_unix: Math.floor(Date.now() / 1000),
      metrics: _metricsRead(),
      limits: {
        rate_window_s: RATE_LIMIT_WINDOW_SECONDS,
        rate_max_requests: RATE_LIMIT_MAX_REQUESTS_PER_WINDOW,
        rate_max_batch_items: RATE_LIMIT_MAX_BATCH_ITEMS_PER_WINDOW,
      },
    });
  }
  return HtmlService.createHtmlOutput(
    "<!DOCTYPE html><html><head><title>My App</title></head>" +
      '<body style="font-family:sans-serif;max-width:600px;margin:40px auto">' +
      "<h1>Welcome</h1><p>This application is running normally.</p>" +
      "</body></html>"
  );
}

function _json(obj) {
  // HtmlService responses can stay on script.google.com for /dev, while
  // ContentService commonly bounces through script.googleusercontent.com.
  // The Python client extracts the JSON payload from the body either way.
  return HtmlService.createHtmlOutput(JSON.stringify(obj)).setXFrameOptionsMode(
    HtmlService.XFrameOptionsMode.ALLOWALL
  );
}

function _okPayload(resp, reqId, startedMs) {
  var body = resp.getContent();
  return {
    s: resp.getResponseCode(),
    h: _sanitizeRespHeaders(_respHeaders(resp)),
    b: Utilities.base64Encode(body),
    rid: reqId,
    t_ms: Date.now() - startedMs,
  };
}

function _sanitizeRespHeaders(headers) {
  var out = {};
  if (!headers || typeof headers !== "object") return out;
  for (var k in headers) {
    if (!Object.prototype.hasOwnProperty.call(headers, k)) continue;
    var lk = String(k).toLowerCase();
    // Avoid hop-by-hop and sensitive proxy metadata in relay response headers.
    if (lk === "connection" || lk === "transfer-encoding" || lk === "keep-alive") continue;
    if (lk.indexOf("proxy-") === 0) continue;
    out[k] = headers[k];
  }
  return out;
}

function _errObj(msg, code, retryable) {
  return { e: msg, c: code, retryable: !!retryable };
}

function _jsonErr(msg, code, reqId, startedMs, retryable) {
  return _json({
    e: String(msg || "error"),
    c: code || "ERR",
    retryable: !!retryable,
    rid: reqId || "",
    t_ms: Math.max(Date.now() - (startedMs || Date.now()), 0),
  });
}

function _applyRateLimit(req) {
  var cache = CacheService.getScriptCache();
  var nowBucket = Math.floor(Date.now() / 1000 / RATE_LIMIT_WINDOW_SECONDS);
  var keyId = String(req.k || "nokey").slice(0, 24);
  var bucketKey = "rl:" + keyId + ":" + nowBucket;
  var raw = cache.get(bucketKey);
  var count = 0;
  var batchUnits = 0;
  if (raw) {
    var parts = raw.split(":");
    count = Number(parts[0] || 0);
    batchUnits = Number(parts[1] || 0);
  }
  count += 1;
  batchUnits += Array.isArray(req.q) ? req.q.length : 1;
  cache.put(bucketKey, String(count) + ":" + String(batchUnits), RATE_LIMIT_WINDOW_SECONDS + 3);
  return {
    blocked:
      count > RATE_LIMIT_MAX_REQUESTS_PER_WINDOW ||
      batchUnits > RATE_LIMIT_MAX_BATCH_ITEMS_PER_WINDOW,
  };
}

function _validateTarget(rawUrl) {
  if (!rawUrl || typeof rawUrl !== "string") return { ok: false, error: "bad url", code: "BAD_URL", retryable: false };
  if (rawUrl.length > MAX_URL_LENGTH) return { ok: false, error: "url too long", code: "BAD_URL", retryable: false };
  var u = String(rawUrl).trim();
  if (!/^https?:\/\//i.test(u)) return { ok: false, error: "bad url", code: "BAD_URL", retryable: false };
  if (!ALLOW_HTTP && /^http:\/\//i.test(u)) return { ok: false, error: "http blocked", code: "BAD_SCHEME", retryable: false };

  var host = _extractHost(u);
  if (!host) return { ok: false, error: "bad host", code: "BAD_HOST", retryable: false };
  if (_isBlockedRelayHost(host)) return { ok: false, error: "direct-only protected host", code: "DIRECT_ONLY", retryable: false };
  if (BLOCK_PRIVATE_IP_TARGETS && _isPrivateIpLiteral(host)) {
    return { ok: false, error: "private ip blocked", code: "SSRF_BLOCKED", retryable: false };
  }
  if (host === "localhost" || host.endsWith(".localhost")) {
    return { ok: false, error: "localhost blocked", code: "SSRF_BLOCKED", retryable: false };
  }
  return { ok: true, url: u };
}

function _isBlockedRelayHost(host) {
  var h = String(host || "").toLowerCase();
  for (var i = 0; i < DENY_RELAY_HOST_SUFFIXES.length; i++) {
    var suf = DENY_RELAY_HOST_SUFFIXES[i].toLowerCase();
    if (h === suf.replace(/^\./, "") || h.endsWith(suf)) return true;
  }
  return false;
}

function _extractHost(url) {
  var m = url.match(/^https?:\/\/([^\/?#:]+|\[[^\]]+\])/i);
  if (!m) return "";
  return m[1].replace(/^\[|\]$/g, "").toLowerCase();
}

function _isPrivateIpLiteral(host) {
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) {
    var p = host.split(".");
    var a = Number(p[0]), b = Number(p[1]);
    if (a === 10 || a === 127 || a === 0) return true;
    if (a === 169 && b === 254) return true;
    if (a === 172 && b >= 16 && b <= 31) return true;
    if (a === 192 && b === 168) return true;
    return false;
  }
  var h = host.toLowerCase();
  if (h === "::1") return true;
  if (h.indexOf("fc") === 0 || h.indexOf("fd") === 0 || h.indexOf("fe80") === 0) return true;
  return false;
}

function _verifyAuth(req) {
  if (!REQUIRE_SIGNED_REQUESTS) return "";
  var ts = Number(req.ts || 0);
  if (!ts) return "missing ts";
  var now = Math.floor(Date.now() / 1000);
  if (Math.abs(now - ts) > MAX_SKEW_SECONDS) return "ts skew";
  var nonce = String(req.nonce || "");
  if (!nonce || nonce.length < 8 || nonce.length > 128) return "bad nonce";
  if (_nonceSeen(nonce)) return "replay";
  var sig = String(req.sig || "").toLowerCase();
  var canonical = _canonicalForSig(req, ts, nonce);
  var calc = Utilities.computeHmacSha256Signature(canonical, SIGNING_KEY)
    .map(function (b) {
      var v = (b < 0 ? b + 256 : b).toString(16);
      return v.length === 1 ? "0" + v : v;
    })
    .join("");
  if (sig !== calc) return "bad sig";
  _markNonce(nonce);
  return "";
}

function _canonicalForSig(req, ts, nonce) {
  var method = String(req.m || "GET").toUpperCase();
  var url = String(req.u || "");
  var bodySha;
  if (Array.isArray(req.q)) {
    var qJson = JSON.stringify(req.q);
    bodySha = _sha256Hex(qJson);
  } else {
    var bodyB64 = String(req.b || "");
    bodySha = _sha256Hex(bodyB64);
  }
  return [String(ts), nonce, method, url, bodySha].join("\n");
}

function _nonceSeen(nonce) {
  var cache = CacheService.getScriptCache();
  return cache.get("nonce:" + nonce) !== null;
}

function _markNonce(nonce) {
  var cache = CacheService.getScriptCache();
  cache.put("nonce:" + nonce, "1", NONCE_TTL_SECONDS);
}

function _isRetryableError(msg) {
  var m = String(msg || "").toLowerCase();
  return m.indexOf("timed out") >= 0 || m.indexOf("reset") >= 0 || m.indexOf("tempor") >= 0;
}

function _newRequestId() {
  return Utilities.getUuid().replace(/-/g, "");
}

function _metricsRead() {
  var p = PropertiesService.getScriptProperties();
  return {
    requests_total: Number(p.getProperty("m:req_total") || 0),
    success_total: Number(p.getProperty("m:req_ok") || 0),
    error_total: Number(p.getProperty("m:req_err") || 0),
    error_auth: Number(p.getProperty("m:err_auth") || 0),
    error_rate_limit: Number(p.getProperty("m:err_rate_limit") || 0),
    error_upstream: Number(p.getProperty("m:err_upstream") || 0),
    avg_latency_ms: Number(p.getProperty("m:lat_avg_ms") || 0),
    samples_latency: Number(p.getProperty("m:lat_samples") || 0),
    last_seen_unix: Number(p.getProperty("m:last_seen_unix") || 0),
  };
}

function _metricsTrackRequest(ok) {
  var p = PropertiesService.getScriptProperties();
  _propAdd(p, "m:req_total", 1);
  if (ok === true) _propAdd(p, "m:req_ok", 1);
  else if (ok === false) _propAdd(p, "m:req_err", 1);
  p.setProperty("m:last_seen_unix", String(Math.floor(Date.now() / 1000)));
}

function _metricsTrackError(code) {
  var c = String(code || "").toUpperCase();
  var p = PropertiesService.getScriptProperties();
  if (c === "AUTH") _propAdd(p, "m:err_auth", 1);
  else if (c === "RATE_LIMIT") _propAdd(p, "m:err_rate_limit", 1);
  else if (c === "UPSTREAM") _propAdd(p, "m:err_upstream", 1);
}

function _metricsTrackLatency(ms) {
  var p = PropertiesService.getScriptProperties();
  var samples = Number(p.getProperty("m:lat_samples") || 0);
  var avg = Number(p.getProperty("m:lat_avg_ms") || 0);
  var nextSamples = samples + 1;
  var nextAvg = nextSamples <= 1 ? ms : (avg + (ms - avg) / nextSamples);
  p.setProperty("m:lat_samples", String(nextSamples));
  p.setProperty("m:lat_avg_ms", String(Math.round(nextAvg * 100) / 100));
}

function _propAdd(p, key, delta) {
  var cur = Number(p.getProperty(key) || 0);
  p.setProperty(key, String(cur + Number(delta || 0)));
}

function _sha256Hex(text) {
  var digest = Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, String(text));
  var out = "";
  for (var i = 0; i < digest.length; i++) {
    var v = digest[i];
    if (v < 0) v += 256;
    var hx = v.toString(16);
    out += hx.length === 1 ? "0" + hx : hx;
  }
  return out;
}
