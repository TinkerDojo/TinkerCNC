namespace TinkerCNC.Protocol;

/// <summary>
/// Packet type byte (the first byte after the length field). Types 0xA1–0xB7 go from the host to the machine,
/// 0x81–0xB7 from the machine to the host (the file-transfer types are used in both directions).
/// </summary>
public enum PacketType : byte
{
    // ---- Host -> machine -------------------------------------------------
    /// <summary>Single real-time character such as '?', '!', '~' or Ctrl-X.</summary>
    ControlSingle = 0xA1,
    /// <summary>A text command line, e.g. G-code, "config-get sd ...", "diagnose".</summary>
    ControlMulti = 0xA2,
    /// <summary>File-transfer command ("transfile"), e.g. "download /sd/config.txt"; starts the XMODEM-like exchange.</summary>
    FileTransferCommand = 0xB0,
    FileMd5 = 0xB1,
    FileView = 0xB2,
    FileData = 0xB3,
    FileEnd = 0xB4,
    FileCancel = 0xB5,
    FileRetry = 0xB6,
    /// <summary>File query. Sent with an empty payload; the machine answers with the same type.</summary>
    FileQuery = 0xB7,

    // ---- Machine -> host -------------------------------------------------
    /// <summary>Status report text: "&lt;State|MPos:x,y,z,a|WPos:...|...&gt;".</summary>
    Status = 0x81,
    /// <summary>Response to the "diagnose" poll.</summary>
    Diagnose = 0x82,
    /// <summary>Load (job/file) progress info; the payload's meaning is not documented here yet.</summary>
    LoadInfo = 0x83,
    LoadFinished = 0x84,
    LoadError = 0x85,
    /// <summary>Normal text output (command replies, config values, wlan scan results, ...).</summary>
    NormalInfo = 0x90,
    /// <summary>Alarm text; also used when the machine refuses a connection because too many clients are connected.</summary>
    AlarmInfo = 0x91,
}
