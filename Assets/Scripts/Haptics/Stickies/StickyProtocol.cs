using System;
using System.Text;

namespace Firefighting.Haptics.Stickies
{
    /// <summary>
    /// Wire format of the Sensorimotor Stickies feedback firmware
    /// (https://stickies.catherineyu.com/docs/develop/ble-api).
    ///
    /// UUIDs and byte layout match the open-source StickyDebugger reference app
    /// (app/StickyDebugger/.../ble/BleConstants.kt and FeedbackBleManager.kt): every Sticky
    /// shares the base UUID XXXXXXXX-7374-6963-6b79-535449434b59 ("stickySTICKY"), and all
    /// multi-byte fields are little-endian.
    /// </summary>
    public static class StickyProtocol
    {
        public const string FeedbackServiceUuid = "00020000-7374-6963-6b79-535449434b59";
        public const string MotorCommandUuid = "00020001-7374-6963-6b79-535449434b59";
        public const string SensingServiceUuid = "00010000-7374-6963-6b79-535449434b59";

        /// <summary>Every Sticky advertises a name starting with this prefix.</summary>
        public const string NamePrefix = "STICKY";

        /// <summary>Motor id that addresses every motor on a feedback Sticky (used by Stop).</summary>
        public const byte AllMotors = 0xFF;

        private const byte CommandStop = 0x00;
        private const byte CommandPlay = 0x01;

        /// <summary>
        /// Motor Command (Play): the firmware renders the whole pattern locally.
        /// Layout: u8 cmd=0x01, u8 motor_id, u16 duration_ms, u8 count, u16 gap_ms, u8 intensity.
        /// </summary>
        public static byte[] Play(byte motorId, int durationMs, int count, int gapMs, int intensity)
        {
            durationMs = Clamp(durationMs, 1, ushort.MaxValue);
            gapMs = Clamp(gapMs, 0, ushort.MaxValue);
            return new[]
            {
                CommandPlay,
                motorId,
                (byte)(durationMs & 0xFF), (byte)(durationMs >> 8),
                (byte)Clamp(count, 1, 255),
                (byte)(gapMs & 0xFF), (byte)(gapMs >> 8),
                (byte)Clamp(intensity, 0, 255),
            };
        }

        /// <summary>Motor Command (Stop). Layout: u8 cmd=0x00, u8 motor_id.</summary>
        public static byte[] Stop(byte motorId = AllMotors) => new[] { CommandStop, motorId };

        /// <summary>
        /// Same rules as the reference app's scanner: the advertised service wins, then the
        /// name (the companion app labels them SENSE and FB).
        /// </summary>
        public static bool IsFeedbackSticky(string name, bool advertisesFeedbackService, bool advertisesSensingService)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (advertisesFeedbackService) return true;
            if (advertisesSensingService) return false;
            string upper = name.ToUpperInvariant();
            if (upper.Contains("SENSE")) return false;
            return upper.Contains("FB") || upper.Contains("FEEDBACK");
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
