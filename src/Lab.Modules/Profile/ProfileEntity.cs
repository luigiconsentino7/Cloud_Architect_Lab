namespace Lab.Modules.Profile;

/// <summary>O perfil local (<c>profile_profiles</c>). O app tem um só usuário: você.</summary>
public sealed class ProfileEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "Você";
    public string Locale { get; set; } = "pt-BR";
    public string DefaultProvider { get; set; } = "aws";
    public string PreferencesJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
