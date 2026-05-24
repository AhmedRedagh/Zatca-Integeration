namespace Girix.Zatca.Exceptions;

public class ZatcaException(string message, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Code { get; } = code;
}

public class ZatcaCryptoException(string message, Exception? inner = null)
    : ZatcaException(message, null, inner);

public class ZatcaXmlException(string message, Exception? inner = null)
    : ZatcaException(message, null, inner);

public class ZatcaOnboardingException(string message, int? code = null)
    : ZatcaException(message, code);

public class ZatcaClearanceException(string message, int? code = null)
    : ZatcaException(message, code);

public class ZatcaReportingException(string message, int? code = null)
    : ZatcaException(message, code);
