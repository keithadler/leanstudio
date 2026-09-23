using Avalonia.Data.Converters;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

public static class StatusConverters
{
    public static readonly IValueConverter IsVerified = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.Verified);
    public static readonly IValueConverter IsConditional = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.RestsOnAssumption);
    public static readonly IValueConverter IsRejected = new FuncValueConverter<VerificationStatus, bool>(s => s == VerificationStatus.Rejected);
}
