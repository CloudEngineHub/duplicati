using Duplicati.WebserverCore.Abstractions;
using Duplicati.WebserverCore.Exceptions;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace Duplicati.WebserverCore.Services;

public class CaptchaService : ICaptchaProvider
{
    private readonly object m_lock = new();
    private readonly Dictionary<string, CaptchaEntry> m_captchas = [];

    private class CaptchaEntry
    {
        public readonly string Answer;
        public readonly string Target;
        public int Attempts;
        public readonly DateTime Expires;

        public CaptchaEntry(string answer, string target)
        {
            Answer = answer;
            Target = target;
            Attempts = 4;
            Expires = DateTime.Now.AddMinutes(2);
        }
    }

    public string CreateCaptcha(string target)
    {
        var answer = CaptchaUtil.CreateRandomAnswer(minlength: 6, maxlength: 6);
        var nonce = Guid.NewGuid().ToString();

        string token;
        using (var ms = new MemoryStream())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(answer + nonce);
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;
            using (var hasher = Library.Utility.HashFactory.CreateHasher(Library.Utility.HashFactory.SHA256))
                token = Library.Utility.Utility.Base64PlainToBase64Url(Convert.ToBase64String(hasher.ComputeHash(ms)));
        }

        lock (m_lock)
        {
            var expired = m_captchas.Where(x => x.Value.Expires < DateTime.Now).Select(x => x.Key).ToArray();
            foreach (var x in expired)
                m_captchas.Remove(x);

            if (m_captchas.Count > 3)
                throw new ServiceUnavailableException("Too many captchas, wait 2 minutes and try again");

            m_captchas[token] = new CaptchaEntry(answer, target);
        }

        return token;
    }

    public byte[] GetCaptchaImage(string token)
    {
        string? answer = null;
        lock (m_lock)
        {
            m_captchas.TryGetValue(token, out var tp);
            if (tp != null && tp.Expires > DateTime.Now)
                answer = tp.Answer;
        }

        if (string.IsNullOrWhiteSpace(answer))
            throw new NotFoundException("No such entry");

        using var image = CaptchaUtil.CreateCaptcha(answer);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    public bool SolvedCaptcha(string token, string target, string answer)
    {
        lock (m_lock)
        {
            m_captchas.TryGetValue(token ?? string.Empty, out var tp);
            if (tp == null)
                return false;

            if (tp.Attempts > 0)
                tp.Attempts--;

            return tp.Attempts >= 0 && string.Equals(tp.Answer, answer, StringComparison.OrdinalIgnoreCase) && tp.Target == target && tp.Expires >= DateTime.Now;
        }
    }


    public static class CaptchaUtil
    {
        /// <summary>
        /// A lookup string with characters to use
        /// </summary>
        private static readonly string DEFAULT_CHARS = "ACDEFGHJKLMNPQRTUVWXY34679";

        /// <summary>
        /// Approximate the size in pixels of text drawn at the given fontsize
        /// </summary>
        private static int ApproxTextWidth(string text, string fontFamily, float fontSize)
        {
            var font = SystemFonts.CreateFont(fontFamily, fontSize);
            var textSize = TextMeasurer.MeasureSize(text, new TextOptions(font) { KerningMode = KerningMode.Standard });
            return (int)textSize.Width;
        }

        /// <summary>
        /// Creates a random answer.
        /// </summary>
        /// <returns>The random answer.</returns>
        /// <param name="allowedchars">The list of allowed chars, supply a character multiple times to change frequency.</param>
        /// <param name="minlength">The minimum answer length.</param>
        /// <param name="maxlength">The maximum answer length.</param>
        public static string CreateRandomAnswer(string? allowedchars = null, int minlength = 10, int maxlength = 12)
        {
            allowedchars = allowedchars ?? DEFAULT_CHARS;
            var rnd = new Random();
            var len = rnd.Next(Math.Min(minlength, maxlength), Math.Max(minlength, maxlength) + 1);
            if (len <= 0)
                throw new ArgumentException($"The values {minlength} and {maxlength} gave a final length of {len} and it must be greater than 0");

            return new string(Enumerable.Range(0, len).Select(x => allowedchars[rnd.Next(0, allowedchars.Length)]).ToArray());
        }

        /// <summary>
        /// Creates a captcha image.
        /// </summary>
        /// <returns>The captcha image.</returns>
        /// <param name="answer">The captcha solution string.</param>
        /// <param name="size">The size of the image, omit to get a size based on the string.</param>
        /// <param name="fontsize">The size of the font used to create the captcha, in pixels.</param>
        public static Image<Rgba32> CreateCaptcha(string answer, Size size = default(Size), float fontsize = 40)
        {
            var fontfamily = "Arial";
            var text_width = ApproxTextWidth(answer, fontfamily, fontsize);
            if (size.Width == 0 || size.Height == 0)
                size = new Size((int)(text_width * 1.2), (int)(fontsize * 1.2));

            var image = new Image<Rgba32>(size.Width, size.Height);
            var rnd = new Random();
            var stray_x = (int)fontsize / 2;
            var stray_y = size.Height / 4;
            var ans_stray_x = (int)fontsize / 3;
            var ans_stray_y = size.Height / 6;
            var font = SystemFonts.CreateFont(fontfamily, fontsize);

            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White);

                // Apply a background string to make it hard to do OCR
                foreach (var color in new[] { Color.Yellow, Color.LightGreen, Color.GreenYellow })
                {
                    var backgroundFont = SystemFonts.CreateFont(fontfamily, fontsize);
                    ctx.DrawText(CreateRandomAnswer(minlength: answer.Length, maxlength: answer.Length), backgroundFont, color, new PointF(rnd.Next(-stray_x, stray_x), rnd.Next(-stray_y, stray_y)));
                }

                var spacing = (size.Width / (int)fontsize) + rnd.Next(0, stray_x);

                // Create vertical background lines
                for (var i = rnd.Next(0, stray_x); i < size.Width; i += spacing)
                {
                    var color = Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                    ctx.DrawLine(color, 1, new PointF(i + rnd.Next(-stray_x, stray_x), rnd.Next(0, stray_y)), new PointF(i + rnd.Next(-stray_x, stray_x), size.Height - rnd.Next(0, stray_y)));
                }

                spacing = (size.Height / (int)fontsize) + rnd.Next(0, stray_y);

                // Create horizontal background lines
                for (var i = rnd.Next(0, stray_y); i < size.Height; i += spacing)
                {
                    var color = Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                    ctx.DrawLine(color, 1, new PointF(rnd.Next(0, stray_x), i + rnd.Next(-stray_y, stray_y)), new PointF(size.Width - rnd.Next(0, stray_x), i + rnd.Next(-stray_y, stray_y)));
                }

                // Draw the actual answer
                var answerColor = Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                ctx.DrawText(answer, font, answerColor, new PointF(((size.Width - text_width) / 2) + rnd.Next(-ans_stray_x, ans_stray_x), ((size.Height - fontsize) / 2) + rnd.Next(-ans_stray_y, ans_stray_y)));
            });

            return image;
        }
    }
}
