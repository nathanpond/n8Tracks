namespace n8Tracks.Domain.Catalog;

/// <summary>A language a Song can be in: its standard code and its English name.</summary>
/// <param name="Code">A BCP 47 primary language subtag: an ISO 639-1 code, or <c>zxx</c>.</param>
/// <param name="Name">Its English name.</param>
public sealed record Language(string Code, string Name);

/// <summary>
/// The languages a Song's release details can name: every ISO 639-1 two-letter code with its English
/// name, plus <see cref="NoLinguisticContent"/> (<c>zxx</c>) for instrumentals. One bundled list,
/// served to the picker and used to check what is sent, so the two never disagree.
/// </summary>
public static class Languages
{
    /// <summary>The code for a recording with no words in any language.</summary>
    public const string NoLinguisticContent = "zxx";

    /// <summary>Every language, by English name (ignoring case).</summary>
    public static IReadOnlyList<Language> All { get; } =
    [
        .. new (string Code, string Name)[]
        {
            ("aa", "Afar"), ("ab", "Abkhazian"), ("ae", "Avestan"), ("af", "Afrikaans"), ("ak", "Akan"),
            ("am", "Amharic"), ("an", "Aragonese"), ("ar", "Arabic"), ("as", "Assamese"), ("av", "Avaric"),
            ("ay", "Aymara"), ("az", "Azerbaijani"), ("ba", "Bashkir"), ("be", "Belarusian"), ("bg", "Bulgarian"),
            ("bi", "Bislama"), ("bm", "Bambara"), ("bn", "Bengali"), ("bo", "Tibetan"), ("br", "Breton"),
            ("bs", "Bosnian"), ("ca", "Catalan"), ("ce", "Chechen"), ("ch", "Chamorro"), ("co", "Corsican"),
            ("cr", "Cree"), ("cs", "Czech"), ("cu", "Church Slavic"), ("cv", "Chuvash"), ("cy", "Welsh"),
            ("da", "Danish"), ("de", "German"), ("dv", "Divehi"), ("dz", "Dzongkha"), ("ee", "Ewe"),
            ("el", "Greek"), ("en", "English"), ("eo", "Esperanto"), ("es", "Spanish"), ("et", "Estonian"),
            ("eu", "Basque"), ("fa", "Persian"), ("ff", "Fulah"), ("fi", "Finnish"), ("fj", "Fijian"),
            ("fo", "Faroese"), ("fr", "French"), ("fy", "Western Frisian"), ("ga", "Irish"), ("gd", "Scottish Gaelic"),
            ("gl", "Galician"), ("gn", "Guarani"), ("gu", "Gujarati"), ("gv", "Manx"), ("ha", "Hausa"),
            ("he", "Hebrew"), ("hi", "Hindi"), ("ho", "Hiri Motu"), ("hr", "Croatian"), ("ht", "Haitian Creole"),
            ("hu", "Hungarian"), ("hy", "Armenian"), ("hz", "Herero"), ("ia", "Interlingua"), ("id", "Indonesian"),
            ("ie", "Interlingue"), ("ig", "Igbo"), ("ii", "Sichuan Yi"), ("ik", "Inupiaq"), ("io", "Ido"),
            ("is", "Icelandic"), ("it", "Italian"), ("iu", "Inuktitut"), ("ja", "Japanese"), ("jv", "Javanese"),
            ("ka", "Georgian"), ("kg", "Kongo"), ("ki", "Kikuyu"), ("kj", "Kuanyama"), ("kk", "Kazakh"),
            ("kl", "Kalaallisut"), ("km", "Khmer"), ("kn", "Kannada"), ("ko", "Korean"), ("kr", "Kanuri"),
            ("ks", "Kashmiri"), ("ku", "Kurdish"), ("kv", "Komi"), ("kw", "Cornish"), ("ky", "Kyrgyz"),
            ("la", "Latin"), ("lb", "Luxembourgish"), ("lg", "Ganda"), ("li", "Limburgish"), ("ln", "Lingala"),
            ("lo", "Lao"), ("lt", "Lithuanian"), ("lu", "Luba-Katanga"), ("lv", "Latvian"), ("mg", "Malagasy"),
            ("mh", "Marshallese"), ("mi", "Māori"), ("mk", "Macedonian"), ("ml", "Malayalam"), ("mn", "Mongolian"),
            ("mr", "Marathi"), ("ms", "Malay"), ("mt", "Maltese"), ("my", "Burmese"), ("na", "Nauru"),
            ("nb", "Norwegian Bokmål"), ("nd", "North Ndebele"), ("ne", "Nepali"), ("ng", "Ndonga"), ("nl", "Dutch"),
            ("nn", "Norwegian Nynorsk"), ("no", "Norwegian"), ("nr", "South Ndebele"), ("nv", "Navajo"), ("ny", "Chichewa"),
            ("oc", "Occitan"), ("oj", "Ojibwa"), ("om", "Oromo"), ("or", "Odia"), ("os", "Ossetian"),
            ("pa", "Punjabi"), ("pi", "Pali"), ("pl", "Polish"), ("ps", "Pashto"), ("pt", "Portuguese"),
            ("qu", "Quechua"), ("rm", "Romansh"), ("rn", "Rundi"), ("ro", "Romanian"), ("ru", "Russian"),
            ("rw", "Kinyarwanda"), ("sa", "Sanskrit"), ("sc", "Sardinian"), ("sd", "Sindhi"), ("se", "Northern Sami"),
            ("sg", "Sango"), ("si", "Sinhala"), ("sk", "Slovak"), ("sl", "Slovenian"), ("sm", "Samoan"),
            ("sn", "Shona"), ("so", "Somali"), ("sq", "Albanian"), ("sr", "Serbian"), ("ss", "Swati"),
            ("st", "Southern Sotho"), ("su", "Sundanese"), ("sv", "Swedish"), ("sw", "Swahili"), ("ta", "Tamil"),
            ("te", "Telugu"), ("tg", "Tajik"), ("th", "Thai"), ("ti", "Tigrinya"), ("tk", "Turkmen"),
            ("tl", "Tagalog"), ("tn", "Tswana"), ("to", "Tongan"), ("tr", "Turkish"), ("ts", "Tsonga"),
            ("tt", "Tatar"), ("tw", "Twi"), ("ty", "Tahitian"), ("ug", "Uyghur"), ("uk", "Ukrainian"),
            ("ur", "Urdu"), ("uz", "Uzbek"), ("ve", "Venda"), ("vi", "Vietnamese"), ("vo", "Volapük"),
            ("wa", "Walloon"), ("wo", "Wolof"), ("xh", "Xhosa"), ("yi", "Yiddish"), ("yo", "Yoruba"),
            ("za", "Zhuang"), ("zh", "Chinese"), ("zu", "Zulu"),
            (NoLinguisticContent, "No linguistic content"),
        }
        .OrderBy(static language => language.Name, StringComparer.OrdinalIgnoreCase)
        .Select(static language => new Language(language.Code, language.Name)),
    ];

    private static readonly Dictionary<string, Language> ByCode = All.ToDictionary(static language => language.Code, StringComparer.Ordinal);

    /// <summary>The language with <paramref name="code"/> exactly as stored (lower case), or null.</summary>
    public static Language? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var language) ? language : null;
}
