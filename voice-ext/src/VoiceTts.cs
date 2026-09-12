using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Oxide.Ext.RustQuestsVoice
{
    /// <summary>
    /// The one API plugins call: render text through any OpenAI-compatible /v1/audio/speech
    /// endpoint (local Kokoro-FastAPI, OpenRouter, ...) into loudness-normalized mono
    /// Ogg Vorbis — the format vanilla Rust clients decode via the cassette mechanism.
    ///
    /// Everything runs on a ThreadPool worker; the callback fires on that worker thread,
    /// so plugins must marshal back (NextTick) before touching game state.
    /// </summary>
    public static class VoiceTts
    {
        public class Request
        {
            public string Endpoint;          // full URL, e.g. http://localhost:8880/v1/audio/speech
            public string ApiKey = "";       // optional Bearer token (hosted endpoints)
            public string Model = "kokoro";
            public string Voice = "af_heart";
            public float Speed = 1f;
            public string Text;
            public float TargetRmsDbfs = -16f;
            public float PeakDbfs = -1f;
            public float Quality = 0.4f;     // libvorbis VBR quality
            public int TimeoutSeconds = 60;
            // Upstream response_format. "wav" (default) self-describes its sample layout;
            // "pcm" is raw headerless 16-bit LE mono at PcmSampleRate — OpenRouter's speech
            // endpoint accepts only mp3|pcm (found live 2026-08-14), and pcm needs no decoder.
            public string Format = "wav";
            public int PcmSampleRate = 24000; // the OpenAI-spec pcm rate; Kokoro's native rate
        }

        public class Result
        {
            public byte[] Ogg;               // null on failure
            public double Seconds;
            public string Error;             // null on success
            public bool Ok => Ogg != null && Error == null;
        }

        public static void Render(Request request, Action<Result> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (request == null || string.IsNullOrEmpty(request.Endpoint) || string.IsNullOrEmpty(request.Text))
            {
                callback(new Result { Error = "missing endpoint or text" });
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new Result();
                try
                {
                    var wav = PostForWav(request);
                    var pcm = request.Format == "pcm"
                        ? VoiceAudio.FromRawPcm16(wav, request.PcmSampleRate)
                        : VoiceAudio.ParseWav(wav);
                    VoiceAudio.Normalize(pcm, request.TargetRmsDbfs, request.PeakDbfs);
                    result.Seconds = pcm.Seconds; // speech only — the pad below is not reported
                    // Rust's cassette playback LOOPS at the ogg's end; a trailing second of
                    // silence puts the loop point where nothing is heard, so callers can stop
                    // the player any time inside the pad without clipping speech or hearing
                    // the first words repeat (found live 2026-08-14).
                    var padded = new float[pcm.Samples.Length + pcm.SampleRate];
                    Array.Copy(pcm.Samples, padded, pcm.Samples.Length);
                    pcm.Samples = padded;
                    result.Ogg = VoiceAudio.EncodeOggVorbis(pcm, request.Quality);
                }
                catch (Exception e)
                {
                    result.Error = e.Message;
                }
                callback(result);
            });
        }

        private static byte[] PostForWav(Request request)
        {
            var body = Encoding.UTF8.GetBytes(new StringBuilder(256)
                .Append("{\"model\":\"").Append(JsonEscape(request.Model))
                .Append("\",\"input\":\"").Append(JsonEscape(request.Text))
                .Append("\",\"voice\":\"").Append(JsonEscape(request.Voice))
                .Append("\",\"speed\":").Append(request.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"response_format\":\"").Append(JsonEscape(string.IsNullOrEmpty(request.Format) ? "wav" : request.Format)).Append("\"}")
                .ToString());

            var http = (HttpWebRequest)WebRequest.Create(request.Endpoint);
            http.Method = "POST";
            http.ContentType = "application/json";
            http.Timeout = request.TimeoutSeconds * 1000;
            http.ReadWriteTimeout = request.TimeoutSeconds * 1000;
            if (!string.IsNullOrEmpty(request.ApiKey))
                http.Headers["Authorization"] = "Bearer " + request.ApiKey;

            using (var stream = http.GetRequestStream())
                stream.Write(body, 0, body.Length);

            try
            {
                using (var response = (HttpWebResponse)http.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
            catch (WebException e)
            {
                var detail = "";
                var errorResponse = e.Response as HttpWebResponse;
                if (errorResponse != null)
                {
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                        detail = reader.ReadToEnd();
                    throw new Exception($"upstream {(int)errorResponse.StatusCode}: {Truncate(detail, 300)}");
                }
                throw new Exception("upstream unreachable: " + e.Message);
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
