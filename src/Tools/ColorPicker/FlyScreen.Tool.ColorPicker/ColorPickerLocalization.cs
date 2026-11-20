using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.ColorPicker;

/// <summary>颜色拾取器文案（独立于主程序 Loc 表）。</summary>
public sealed class ColorPickerLocalization : IToolLocalization
{
    public static ColorPickerLocalization Instance { get; } = new();

    static ColorPickerLocalization() => EnsureRegistered();

    public string SourceId => "color-picker";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } =
        ToolLocalizationBuilder.Build(Zh(), En(), Extras());

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.ColorPicker.Name"] = "颜色拾取器",
        ["Tool.ColorPicker.Summary"] = "用滴管拾取屏幕任意位置颜色，并转换为 HEX / RGB / HSL / CMYK 等常用格式。",
        ["Tool.ColorPicker.Pick"] = "颜色拾取器",
        ["Tool.ColorPicker.Picking"] = "拾取中… 再点取消",
        ["Tool.ColorPicker.Preview"] = "颜色预览",
        ["Tool.ColorPicker.Formats"] = "颜色格式",
        ["Tool.ColorPicker.Copied"] = "已复制",
        ["Tool.ColorPicker.Hint"] = "点击「颜色拾取器」后鼠标变为滴管；移动时实时预览，单击锁定颜色；再点按钮可取消拾取。",
        ["Tool.ColorPicker.Unsupported"] = "当前平台不支持屏幕取色。"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.ColorPicker.Name"] = "Color picker",
        ["Tool.ColorPicker.Summary"] = "Eyedrop any on-screen pixel and convert it to HEX / RGB / HSL / CMYK and other common formats.",
        ["Tool.ColorPicker.Pick"] = "Color picker",
        ["Tool.ColorPicker.Picking"] = "Picking… click again to cancel",
        ["Tool.ColorPicker.Preview"] = "Preview",
        ["Tool.ColorPicker.Formats"] = "Color formats",
        ["Tool.ColorPicker.Copied"] = "Copied",
        ["Tool.ColorPicker.Hint"] = "Click Color picker for an eyedropper cursor; move to preview live, click to lock; click the button again to cancel.",
        ["Tool.ColorPicker.Unsupported"] = "Screen sampling is not supported on this platform."
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Extras()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-TW"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "顏色拾取器",
                ["Tool.ColorPicker.Summary"] = "用滴管拾取螢幕任意位置顏色，並轉換為 HEX / RGB / HSL / CMYK 等常用格式。",
                ["Tool.ColorPicker.Pick"] = "顏色拾取器",
                ["Tool.ColorPicker.Picking"] = "拾取中… 再點取消",
                ["Tool.ColorPicker.Preview"] = "顏色預覽",
                ["Tool.ColorPicker.Formats"] = "顏色格式",
                ["Tool.ColorPicker.Copied"] = "已複製",
                ["Tool.ColorPicker.Hint"] = "點擊「顏色拾取器」後滑鼠變為滴管；移動時即時預覽，單擊鎖定顏色；再點按鈕可取消拾取。",
                ["Tool.ColorPicker.Unsupported"] = "目前平台不支援螢幕取色。"
            },
            ["ja-JP"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "カラーピッカー",
                ["Tool.ColorPicker.Summary"] = "スポイトで画面上の任意の色を取得し、HEX / RGB / HSL / CMYK など一般的な形式に変換します。",
                ["Tool.ColorPicker.Pick"] = "カラーピッカー",
                ["Tool.ColorPicker.Picking"] = "取得中… 再クリックでキャンセル",
                ["Tool.ColorPicker.Preview"] = "プレビュー",
                ["Tool.ColorPicker.Formats"] = "色形式",
                ["Tool.ColorPicker.Copied"] = "コピーしました",
                ["Tool.ColorPicker.Hint"] = "「カラーピッカー」をクリックするとスポイトカーソルになります。移動でライブプレビュー、クリックで確定。もう一度ボタンを押すとキャンセル。",
                ["Tool.ColorPicker.Unsupported"] = "このプラットフォームでは画面の色取得に対応していません。"
            },
            ["ko-KR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "색 선택기",
                ["Tool.ColorPicker.Summary"] = "스포이드로 화면의 아무 위치 색을 가져와 HEX / RGB / HSL / CMYK 등 일반 형식으로 변환합니다.",
                ["Tool.ColorPicker.Pick"] = "색 선택기",
                ["Tool.ColorPicker.Picking"] = "선택 중… 다시 클릭하면 취소",
                ["Tool.ColorPicker.Preview"] = "미리보기",
                ["Tool.ColorPicker.Formats"] = "색 형식",
                ["Tool.ColorPicker.Copied"] = "복사됨",
                ["Tool.ColorPicker.Hint"] = "「색 선택기」를 클릭하면 스포이드 커서가 됩니다. 이동 시 실시간 미리보기, 클릭으로 고정, 버튼을 다시 누르면 취소합니다.",
                ["Tool.ColorPicker.Unsupported"] = "이 플랫폼에서는 화면 색 샘플링을 지원하지 않습니다."
            },
            ["fr-FR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Pipette de couleur",
                ["Tool.ColorPicker.Summary"] = "Prélevez n’importe quel pixel à l’écran et convertissez-le en HEX / RGB / HSL / CMYK et autres formats courants.",
                ["Tool.ColorPicker.Pick"] = "Pipette de couleur",
                ["Tool.ColorPicker.Picking"] = "Prélèvement… recliquez pour annuler",
                ["Tool.ColorPicker.Preview"] = "Aperçu",
                ["Tool.ColorPicker.Formats"] = "Formats de couleur",
                ["Tool.ColorPicker.Copied"] = "Copié",
                ["Tool.ColorPicker.Hint"] = "Cliquez sur Pipette pour un curseur pipette ; déplacez pour un aperçu en direct, cliquez pour verrouiller ; recliquez pour annuler.",
                ["Tool.ColorPicker.Unsupported"] = "Le prélèvement d’écran n’est pas pris en charge sur cette plateforme."
            },
            ["de-DE"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Farbpipette",
                ["Tool.ColorPicker.Summary"] = "Beliebige Bildschirmfarbe mit der Pipette aufnehmen und in HEX / RGB / HSL / CMYK und andere gängige Formate umwandeln.",
                ["Tool.ColorPicker.Pick"] = "Farbpipette",
                ["Tool.ColorPicker.Picking"] = "Aufnahme… erneut klicken zum Abbrechen",
                ["Tool.ColorPicker.Preview"] = "Vorschau",
                ["Tool.ColorPicker.Formats"] = "Farbformate",
                ["Tool.ColorPicker.Copied"] = "Kopiert",
                ["Tool.ColorPicker.Hint"] = "Klicken Sie auf Farbpipette für den Pipettenzeiger; bewegen zum Live-Preview, klicken zum Fixieren; erneut klicken zum Abbrechen.",
                ["Tool.ColorPicker.Unsupported"] = "Bildschirmfarben werden auf dieser Plattform nicht unterstützt."
            },
            ["es-ES"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Selector de color",
                ["Tool.ColorPicker.Summary"] = "Muestree cualquier píxel de la pantalla y conviértalo a HEX / RGB / HSL / CMYK y otros formatos habituales.",
                ["Tool.ColorPicker.Pick"] = "Selector de color",
                ["Tool.ColorPicker.Picking"] = "Muestreando… pulse de nuevo para cancelar",
                ["Tool.ColorPicker.Preview"] = "Vista previa",
                ["Tool.ColorPicker.Formats"] = "Formatos de color",
                ["Tool.ColorPicker.Copied"] = "Copiado",
                ["Tool.ColorPicker.Hint"] = "Pulse Selector de color para el gotero; mueva para vista previa en vivo, pulse para fijar; pulse de nuevo para cancelar.",
                ["Tool.ColorPicker.Unsupported"] = "El muestreo de pantalla no es compatible en esta plataforma."
            },
            ["pt-BR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Seletor de cores",
                ["Tool.ColorPicker.Summary"] = "Capture qualquer pixel da tela e converta para HEX / RGB / HSL / CMYK e outros formatos comuns.",
                ["Tool.ColorPicker.Pick"] = "Seletor de cores",
                ["Tool.ColorPicker.Picking"] = "Capturando… clique de novo para cancelar",
                ["Tool.ColorPicker.Preview"] = "Pré-visualização",
                ["Tool.ColorPicker.Formats"] = "Formatos de cor",
                ["Tool.ColorPicker.Copied"] = "Copiado",
                ["Tool.ColorPicker.Hint"] = "Clique em Seletor de cores para o conta-gotas; mova para pré-visualizar, clique para travar; clique de novo para cancelar.",
                ["Tool.ColorPicker.Unsupported"] = "A amostragem de tela não é suportada nesta plataforma."
            },
            ["ru-RU"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Пипетка цвета",
                ["Tool.ColorPicker.Summary"] = "Снимите цвет любого пикселя на экране и преобразуйте в HEX / RGB / HSL / CMYK и другие распространённые форматы.",
                ["Tool.ColorPicker.Pick"] = "Пипетка цвета",
                ["Tool.ColorPicker.Picking"] = "Выбор… нажмите ещё раз, чтобы отменить",
                ["Tool.ColorPicker.Preview"] = "Просмотр",
                ["Tool.ColorPicker.Formats"] = "Форматы цвета",
                ["Tool.ColorPicker.Copied"] = "Скопировано",
                ["Tool.ColorPicker.Hint"] = "Нажмите «Пипетка цвета» для курсора-пипетки; двигайте для живого просмотра, щёлкните для фиксации; нажмите снова для отмены.",
                ["Tool.ColorPicker.Unsupported"] = "Захват цвета экрана не поддерживается на этой платформе."
            },
            ["ar-SA"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "مُنتقي الألوان",
                ["Tool.ColorPicker.Summary"] = "التقط أي لون على الشاشة وحوّله إلى HEX / RGB / HSL / CMYK وغيرها من الصيغ الشائعة.",
                ["Tool.ColorPicker.Pick"] = "مُنتقي الألوان",
                ["Tool.ColorPicker.Picking"] = "جارٍ الالتقاط… انقر مجددًا للإلغاء",
                ["Tool.ColorPicker.Preview"] = "معاينة",
                ["Tool.ColorPicker.Formats"] = "صيغ الألوان",
                ["Tool.ColorPicker.Copied"] = "تم النسخ",
                ["Tool.ColorPicker.Hint"] = "انقر مُنتقي الألوان لمؤشر القطّارة؛ حرّك للمعاينة المباشرة، انقر للتثبيت؛ انقر الزر مجددًا للإلغاء.",
                ["Tool.ColorPicker.Unsupported"] = "أخذ عيّنة من الشاشة غير مدعوم على هذه المنصة."
            },
            ["hi-IN"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "रंग चयनकर्ता",
                ["Tool.ColorPicker.Summary"] = "स्क्रीन पर किसी भी पिक्सेल का रंग लें और HEX / RGB / HSL / CMYK आदि सामान्य प्रारूपों में बदलें।",
                ["Tool.ColorPicker.Pick"] = "रंग चयनकर्ता",
                ["Tool.ColorPicker.Picking"] = "चयन हो रहा है… रद्द करने के लिए फिर क्लिक करें",
                ["Tool.ColorPicker.Preview"] = "पूर्वावलोकन",
                ["Tool.ColorPicker.Formats"] = "रंग प्रारूप",
                ["Tool.ColorPicker.Copied"] = "कॉपी हो गया",
                ["Tool.ColorPicker.Hint"] = "आईड्रॉपर कर्सर के लिए रंग चयनकर्ता पर क्लिक करें; लाइव पूर्वावलोकन के लिए घुमाएँ, लॉक करने के लिए क्लिक करें; रद्द करने के लिए फिर बटन दबाएँ।",
                ["Tool.ColorPicker.Unsupported"] = "इस प्लेटफ़ॉर्म पर स्क्रीन नमूना समर्थित नहीं है।"
            },
            ["th-TH"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "ตัวเลือกสี",
                ["Tool.ColorPicker.Summary"] = "ดูดสีพิกเซลใดก็ได้บนหน้าจอ แล้วแปลงเป็น HEX / RGB / HSL / CMYK และรูปแบบที่ใช้บ่อยอื่นๆ",
                ["Tool.ColorPicker.Pick"] = "ตัวเลือกสี",
                ["Tool.ColorPicker.Picking"] = "กำลังเลือก… คลิกอีกครั้งเพื่อยกเลิก",
                ["Tool.ColorPicker.Preview"] = "ตัวอย่าง",
                ["Tool.ColorPicker.Formats"] = "รูปแบบสี",
                ["Tool.ColorPicker.Copied"] = "คัดลอกแล้ว",
                ["Tool.ColorPicker.Hint"] = "คลิกตัวเลือกสีเพื่อใช้เคอร์เซอร์หยดสี เลื่อนเพื่อดูตัวอย่างแบบสด คลิกเพื่อล็อก สี คลิกปุ่มอีกครั้งเพื่อยกเลิก",
                ["Tool.ColorPicker.Unsupported"] = "แพลตฟอร์มนี้ไม่รองรับการสุ่มตัวอย่างสีจากหน้าจอ"
            },
            ["vi-VN"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Ống hút màu",
                ["Tool.ColorPicker.Summary"] = "Lấy màu bất kỳ điểm ảnh trên màn hình và chuyển sang HEX / RGB / HSL / CMYK cùng các định dạng phổ biến khác.",
                ["Tool.ColorPicker.Pick"] = "Ống hút màu",
                ["Tool.ColorPicker.Picking"] = "Đang lấy màu… nhấn lại để hủy",
                ["Tool.ColorPicker.Preview"] = "Xem trước",
                ["Tool.ColorPicker.Formats"] = "Định dạng màu",
                ["Tool.ColorPicker.Copied"] = "Đã sao chép",
                ["Tool.ColorPicker.Hint"] = "Nhấn Ống hút màu để hiện con trỏ ống hút; di chuyển để xem trước trực tiếp, nhấn để khóa; nhấn lại nút để hủy.",
                ["Tool.ColorPicker.Unsupported"] = "Nền tảng này không hỗ trợ lấy mẫu màu màn hình."
            },
            ["id-ID"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Pemilih warna",
                ["Tool.ColorPicker.Summary"] = "Ambil warna piksel mana pun di layar dan konversikan ke HEX / RGB / HSL / CMYK serta format umum lainnya.",
                ["Tool.ColorPicker.Pick"] = "Pemilih warna",
                ["Tool.ColorPicker.Picking"] = "Mengambil… klik lagi untuk membatalkan",
                ["Tool.ColorPicker.Preview"] = "Pratinjau",
                ["Tool.ColorPicker.Formats"] = "Format warna",
                ["Tool.ColorPicker.Copied"] = "Disalin",
                ["Tool.ColorPicker.Hint"] = "Klik Pemilih warna untuk kursor eyedropper; gerakkan untuk pratinjau langsung, klik untuk mengunci; klik lagi untuk membatalkan.",
                ["Tool.ColorPicker.Unsupported"] = "Pengambilan sampel layar tidak didukung di platform ini."
            },
            ["tr-TR"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Renk seçici",
                ["Tool.ColorPicker.Summary"] = "Ekrandaki herhangi bir pikseli damlalıklayın ve HEX / RGB / HSL / CMYK gibi yaygın biçimlere dönüştürün.",
                ["Tool.ColorPicker.Pick"] = "Renk seçici",
                ["Tool.ColorPicker.Picking"] = "Seçiliyor… iptal için tekrar tıklayın",
                ["Tool.ColorPicker.Preview"] = "Önizleme",
                ["Tool.ColorPicker.Formats"] = "Renk biçimleri",
                ["Tool.ColorPicker.Copied"] = "Kopyalandı",
                ["Tool.ColorPicker.Hint"] = "Damlalık imleci için Renk seçici’ye tıklayın; canlı önizleme için hareket ettirin, kilitlemek için tıklayın; iptal için düğmeye tekrar tıklayın.",
                ["Tool.ColorPicker.Unsupported"] = "Ekran örnekleme bu platformda desteklenmiyor."
            },
            ["it-IT"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Contagocce colore",
                ["Tool.ColorPicker.Summary"] = "Campiona qualsiasi pixel sullo schermo e convertilo in HEX / RGB / HSL / CMYK e altri formati comuni.",
                ["Tool.ColorPicker.Pick"] = "Contagocce colore",
                ["Tool.ColorPicker.Picking"] = "Campionamento… clic di nuovo per annullare",
                ["Tool.ColorPicker.Preview"] = "Anteprima",
                ["Tool.ColorPicker.Formats"] = "Formati colore",
                ["Tool.ColorPicker.Copied"] = "Copiato",
                ["Tool.ColorPicker.Hint"] = "Fai clic su Contagocce per il cursore; muovi per anteprima live, clic per bloccare; clic di nuovo per annullare.",
                ["Tool.ColorPicker.Unsupported"] = "Il campionamento schermo non è supportato su questa piattaforma."
            },
            ["nl-NL"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Kleurkiezer",
                ["Tool.ColorPicker.Summary"] = "Pak elke schermpixel met de pipet en zet om naar HEX / RGB / HSL / CMYK en andere gangbare formaten.",
                ["Tool.ColorPicker.Pick"] = "Kleurkiezer",
                ["Tool.ColorPicker.Picking"] = "Bezig… klik opnieuw om te annuleren",
                ["Tool.ColorPicker.Preview"] = "Voorbeeld",
                ["Tool.ColorPicker.Formats"] = "Kleurformaten",
                ["Tool.ColorPicker.Copied"] = "Gekopieerd",
                ["Tool.ColorPicker.Hint"] = "Klik op Kleurkiezer voor een pipetcursor; beweeg voor live voorbeeld, klik om te vergrendelen; klik opnieuw om te annuleren.",
                ["Tool.ColorPicker.Unsupported"] = "Schermsampling wordt op dit platform niet ondersteund."
            },
            ["pl-PL"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Próbnik kolorów",
                ["Tool.ColorPicker.Summary"] = "Pobierz kolor dowolnego piksela na ekranie i przekształć na HEX / RGB / HSL / CMYK oraz inne popularne formaty.",
                ["Tool.ColorPicker.Pick"] = "Próbnik kolorów",
                ["Tool.ColorPicker.Picking"] = "Pobieranie… kliknij ponownie, aby anulować",
                ["Tool.ColorPicker.Preview"] = "Podgląd",
                ["Tool.ColorPicker.Formats"] = "Formaty kolorów",
                ["Tool.ColorPicker.Copied"] = "Skopiowano",
                ["Tool.ColorPicker.Hint"] = "Kliknij Próbnik kolorów, by uzyskać kursor pipety; przesuwaj dla podglądu na żywo, kliknij, by zablokować; kliknij ponownie, by anulować.",
                ["Tool.ColorPicker.Unsupported"] = "Próbkowanie ekranu nie jest obsługiwane na tej platformie."
            },
            ["uk-UA"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tool.ColorPicker.Name"] = "Піпетка кольору",
                ["Tool.ColorPicker.Summary"] = "Зніміть колір будь-якого пікселя на екрані та перетворіть на HEX / RGB / HSL / CMYK та інші поширені формати.",
                ["Tool.ColorPicker.Pick"] = "Піпетка кольору",
                ["Tool.ColorPicker.Picking"] = "Вибір… натисніть ще раз, щоб скасувати",
                ["Tool.ColorPicker.Preview"] = "Перегляд",
                ["Tool.ColorPicker.Formats"] = "Формати кольору",
                ["Tool.ColorPicker.Copied"] = "Скопійовано",
                ["Tool.ColorPicker.Hint"] = "Натисніть «Піпетка кольору» для курсора-піпетки; рухайте для живого перегляду, клацніть для фіксації; натисніть знову для скасування.",
                ["Tool.ColorPicker.Unsupported"] = "Вибірка кольору екрана не підтримується на цій платформі."
            }
        };
}
