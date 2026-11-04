using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Common;

/// <summary>A problem the user can fix, shown to them as is (400 unless another status is given).</summary>
public sealed class AppException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One cell of a CSV file someone will open in Excel. Text that starts like a formula (=, +, -, @, a tab)
/// gets a leading apostrophe so Excel shows it instead of running it: names typed by customers in the public shop end
/// up in the VAT and payroll files. Numbers (including negative amounts) are left as they are.</summary>
public static class CsvCell
{
    public static string Of(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}

/// <summary>The logo printed on the business's documents. Only PNG, JPEG or WebP (never SVG, which can carry
/// script), checked by its first bytes rather than by what it claims to be, and small: the browser resizes it first.</summary>
public static class Letterhead
{
    public const int MaxLogoBytes = 300 * 1024;

    public static string CheckLogo(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return ""; // removing the logo
        var m = System.Text.RegularExpressions.Regex.Match(dataUrl, @"^data:image/(png|jpeg|webp);base64,([A-Za-z0-9+/=]+)$");
        if (!m.Success) throw new AppException("Use a PNG, JPG or WebP picture for the logo.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(m.Groups[2].Value); }
        catch (FormatException) { throw new AppException("That picture couldn’t be read. Try another file."); }
        if (bytes.Length > MaxLogoBytes) throw new AppException("That logo is too large. Use one under 300 KB.");
        var ok = m.Groups[1].Value switch
        {
            "png" => bytes is [0x89, 0x50, 0x4E, 0x47, ..],
            "jpeg" => bytes is [0xFF, 0xD8, 0xFF, ..],
            _ => bytes.Length > 12 && bytes[..4].SequenceEqual("RIFF"u8.ToArray()) && bytes[8..12].SequenceEqual("WEBP"u8.ToArray()),
        };
        if (!ok) throw new AppException("That file isn’t the picture it says it is. Use a PNG, JPG or WebP.");
        return dataUrl;
    }
}

public static class Clock
{
    /// <summary>Today in Kenya (EAT, UTC+3; no daylight saving).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
}

public sealed class Numbers(ErpDb db)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SequenceGate> Gates = new();

    private sealed class SequenceGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int? CurrentNext;
    }

    private static async Task<int> MaxExistingNumber(ErpDb db, string key)
    {
        try
        {
            List<string> existing = key switch
            {
                "INV" or "QT" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.SalesDocs.Where(d => d.Number.StartsWith(key + "-")).Select(d => d.Number)),
                "PO" or "BILL" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.PurchaseDocs.Where(d => d.Number.StartsWith(key + "-")).Select(d => d.Number)),
                "C" or "CUST" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Customers.Where(d => d.Number.StartsWith(key + "-")).Select(d => d.Number)),
                "S" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Suppliers.Where(d => d.Number.StartsWith("S-")).Select(d => d.Number)),
                "E" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Employees.Where(d => d.Number.StartsWith("E-")).Select(d => d.Number)),
                "L" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Leads.Where(d => d.Number.StartsWith("L-")).Select(d => d.Number)),
                "TRIP" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.DispatchTrips.Where(d => d.TripNumber.StartsWith("TRIP-")).Select(d => d.TripNumber)),
                "WB" => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.DeliveryItems.Where(d => d.WaybillNumber.StartsWith("WB-")).Select(d => d.WaybillNumber)),
                _ => []
            };

            var max = 0;
            foreach (var num in existing)
            {
                var dash = num.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(num[(dash + 1)..], out var parsed) && parsed > max)
                    max = parsed;
            }
            return max;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The next number in a series for this company: Next("INV") → "INV-0001".</summary>
    public async Task<string> Next(string key)
    {
        // A series started earlier in this same save isn't in the database yet: use that one, not a second.
        var localSeq = db.Sequences.Local.FirstOrDefault(s => s.Key == key && s.CompanyId == db.CompanyId);
        if (localSeq is not null)
        {
            var localN = localSeq.Next++;
            return $"{key}-{localN:0000}";
        }

        var lockKey = $"{db.CompanyId}:{key}";
        var gate = Gates.GetOrAdd(lockKey, _ => new SequenceGate());
        await gate.Semaphore.WaitAsync();
        try
        {
            var seq = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(db.Sequences, s => s.Key == key);
            var maxExisting = await MaxExistingNumber(db, key);

            if (seq is null)
            {
                var initialNext = Math.Max(gate.CurrentNext ?? 1, maxExisting + 1);
                seq = new Sequence { Key = key, CompanyId = db.CompanyId, Next = initialNext };
                db.Sequences.Add(seq);
            }
            else
            {
                if (gate.CurrentNext.HasValue && gate.CurrentNext.Value > seq.Next)
                {
                    seq.Next = gate.CurrentNext.Value;
                }
                if (seq.Next <= maxExisting)
                {
                    seq.Next = maxExisting + 1;
                }
            }

            var n = seq.Next++;
            gate.CurrentNext = seq.Next;
            return $"{key}-{n:0000}";
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}

public static class Phones
{
    /// <summary>0712 345 678 / +254712345678 / 712345678 → 254712345678; null if it isn't a Kenyan mobile.</summary>
    public static string? ToMsisdn(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        var local = digits.StartsWith("254") ? digits[3..] : digits.StartsWith('0') ? digits[1..] : digits;
        return local.Length == 9 && (local[0] == '7' || local[0] == '1') ? $"254{local}" : null;
    }
}

/// <summary>A page of results with the total, so lists can say "41 of 1,557".</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

public static class Paging
{
    public static (int Skip, int Take) Of(int? skip, int? take) => (Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200));
}

public static class Money
{
    /// <summary>"KSh 12,500" (whole shillings), for messages people read.</summary>
    public static string Ksh(long cents) => "KSh " + (cents / 100m).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
