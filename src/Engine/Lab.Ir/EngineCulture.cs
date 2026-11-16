using System.Globalization;

namespace Lab.Ir;

/// <summary>
/// O motor sempre formata em pt-BR, independente da cultura da máquina: as mensagens fazem parte do resultado,
/// e o resultado precisa ser idêntico byte a byte em qualquer computador. Cada ponto de entrada público roda aqui dentro.
/// A cultura flui para as tarefas paralelas (ela viaja no ExecutionContext).
/// </summary>
public static class EngineCulture
{
    public static CultureInfo PtBr { get; } = CultureInfo.GetCultureInfo("pt-BR");

    public static T Run<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        if (previous.Name == PtBr.Name) return action();
        CultureInfo.CurrentCulture = PtBr;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
