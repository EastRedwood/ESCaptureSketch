using System.Globalization;

namespace ESCaptureSketch;

/// <summary>
/// 화면 언어(한국어 / 영어). 소스의 한국어 원문이 열쇠이고, 영어 문구는 Strings.En.cs 에 있다.
/// 프로그램을 시작할 때 한 번 정한다 (설정에서 바꾸면 다시 시작해야 적용된다).
/// </summary>
public static partial class L
{
    public static bool English { get; private set; }

    /// <summary>"ko", "en", 그 밖(System)은 Windows 표시 언어가 한국어면 한국어, 아니면 영어.</summary>
    public static void Apply(string? mode)
        => English = mode == "en" || (mode != "ko" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ko");

    public static string T(string korean) => English && En.TryGetValue(korean, out var english) ? english : korean;

    /// <summary>값이 끼는 문구: 원문의 {0}, {1} 자리에 넣는다.</summary>
    public static string F(string korean, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(korean), args);
}
