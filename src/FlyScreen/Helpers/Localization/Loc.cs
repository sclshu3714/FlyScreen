using System.Globalization;

namespace FlyScreen.Helpers.Localization;

/// <summary>
/// 多语言：当前语言 → 英文 → 中文。
/// </summary>
public static class Loc
{
    public const string DefaultCulture = "zh-CN";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = BuildTables();

    public static string Culture { get; private set; } = DefaultCulture;

    public static event Action? Changed;

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("zh-CN", "中文（简体）", "Chinese (Simplified)"),
        new("zh-TW", "中文（繁體）", "Chinese (Traditional)"),
        new("en-US", "English", "English"),
        new("ja-JP", "日本語", "Japanese"),
        new("ko-KR", "한국어", "Korean"),
        new("fr-FR", "Français", "French"),
        new("de-DE", "Deutsch", "German"),
        new("es-ES", "Español", "Spanish"),
        new("pt-BR", "Português", "Portuguese"),
        new("ru-RU", "Русский", "Russian"),
        new("ar-SA", "العربية", "Arabic"),
        new("hi-IN", "हिन्दी", "Hindi"),
        new("th-TH", "ไทย", "Thai"),
        new("vi-VN", "Tiếng Việt", "Vietnamese"),
        new("id-ID", "Bahasa Indonesia", "Indonesian"),
        new("tr-TR", "Türkçe", "Turkish"),
        new("it-IT", "Italiano", "Italian"),
        new("nl-NL", "Nederlands", "Dutch"),
        new("pl-PL", "Polski", "Polish"),
        new("uk-UA", "Українська", "Ukrainian"),
    ];

    public static void SetCulture(string? culture)
    {
        var code = string.IsNullOrWhiteSpace(culture) ? DefaultCulture : culture.Trim();
        if (!Tables.ContainsKey(code))
            code = DefaultCulture;
        if (string.Equals(Culture, code, StringComparison.OrdinalIgnoreCase))
            return;
        Culture = code;
        try { CultureInfo.CurrentUICulture = new CultureInfo(code); } catch { /* ignore */ }
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (TryGet(Culture, key, out var s)) return s;
        if (!string.Equals(Culture, "en-US", StringComparison.OrdinalIgnoreCase)
            && TryGet("en-US", key, out s))
            return s;
        if (TryGet(DefaultCulture, key, out s)) return s;
        return key;
    }

    private static bool TryGet(string culture, string key, out string value)
    {
        value = "";
        return Tables.TryGetValue(culture, out var table) && table.TryGetValue(key, out value!);
    }

    private static Dictionary<string, Dictionary<string, string>> BuildTables()
    {
        var zh = Zh();
        var en = En();
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = zh,
            ["en-US"] = en,
            ["zh-TW"] = LocCatalogs.ZhTw(zh),
            ["ja-JP"] = LocCatalogs.Ja(en),
            ["ko-KR"] = LocCatalogs.Ko(en),
            ["fr-FR"] = LocCatalogs.Fr(en),
            ["de-DE"] = LocCatalogs.De(en),
            ["es-ES"] = LocCatalogs.Es(en),
            ["pt-BR"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Básico"), ("Tab.Options", "Avançado"), ("Tab.Network", "Rede"), ("Tab.Transfer", "Transferências"),
                ("Btn.Apply", "Aplicar"), ("Btn.HideTray", "Ocultar na bandeja"), ("Btn.Exit", "Sair"),
                ("Btn.About", "Sobre"), ("Btn.Help", "? Ajuda"),
                ("Feat.Title", "Funções"), ("Beh.Title", "Comportamento"), ("Hot.Title", "Atalhos de teclado"),
                ("Hot.Switch", "Trocar PC (Ctrl+Alt+)"), ("Hot.Off", "Desativado"),
                ("Hot.Settings", "Mostrar definições (Ctrl+Alt+)"), ("Hot.Reconnect", "Reconectar (Ctrl+Alt+)"),
                ("Hot.Exit", "Sair (Ctrl+Alt+)"), ("Hot.Lock", "Bloquear PC (Ctrl+Alt+)"),
                ("Sys.Title", "Sistema"), ("Sys.Style", "Tema"), ("Sys.StyleLight", "Claro (padrão)"), ("Sys.StyleDark", "Escuro"),
                ("Sys.Language", "Idioma"),
                ("Sys.StyleTip", "Tema de cores da interface: claro ou escuro."),
                ("Sys.LangTip", "Idioma da interface. Textos sem tradução usam inglês/chinês."),
                ("Sys.Applied", "Definições aplicadas; layout sincronizado"),
                ("Opt.WrapMouse", "Rato circular"), ("Opt.ShareClipboard", "Partilhar área de transferência"),
                ("Opt.TransferFile", "Ativar transferência de ficheiros"), ("Opt.HideMouse", "Ocultar cursor local no remoto"),
                ("Opt.DrawCursor", "Desenhar cursor (reservado)"), ("Opt.SameSubnet", "Apenas mesma sub-rede"),
                ("Opt.ShowStatus", "Mostrar mensagens de estado"), ("Opt.AutoStart", "Iniciar com o Windows e ligar"),
                ("Opt.BlockSaver", "Bloquear proteção de ecrã"), ("Opt.MoveRel", "Mover rato em relativo"),
                ("Opt.BlockCorner", "Bloquear mudança nos cantos"), ("Opt.EasyMouse", "Easy Mouse (bordo para mudar)"),
                ("Tip.WrapMouse", "Liga as extremidades esquerda/direita (ou cima/baixo). Só muda se o vizinho tiver PC."),
                ("Tip.ShareClipboard", "Partilha a área de transferência entre PCs ligados."),
                ("Tip.TransferFile", "Permite transferência sob pedido e envio direto de ficheiros."),
                ("Tip.HideMouse", "Mostra só o cursor do PC ativo. Oculta o local ao controlar remoto."),
                ("Tip.DrawCursor", "Reservado: desenhar cursor virtual. Ainda não implementado."),
                ("Tip.SameSubnet", "Só permite pares na mesma sub-rede."),
                ("Tip.ShowStatus", "Mostra o estado na parte inferior da janela."),
                ("Tip.AutoStart", "Inicia ~15 s após o início de sessão via Agendador de Tarefas."),
                ("Tip.BlockSaver", "Impede a proteção de ecrã noutros PCs durante o controlo remoto."),
                ("Tip.MoveRel", "Envia movimentos relativos. Melhor desativado no ambiente de trabalho."),
                ("Tip.BlockCorner", "Não muda perto dos cantos do ecrã."),
                ("Tip.EasyMouse", "Sair pelo bordo muda para o PC adjacente.")),
            ["ru-RU"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Основное"), ("Tab.Options", "Дополнительно"), ("Tab.Network", "Сеть"), ("Tab.Transfer", "Передача"),
                ("Btn.Apply", "Применить"), ("Btn.HideTray", "Скрыть в трей"), ("Btn.Exit", "Выход"),
                ("Btn.About", "О программе"), ("Btn.Help", "? Справка"),
                ("Feat.Title", "Функции"), ("Beh.Title", "Поведение"), ("Hot.Title", "Горячие клавиши"),
                ("Hot.Switch", "Смена ПК (Ctrl+Alt+)"), ("Hot.Off", "Выкл."),
                ("Hot.Settings", "Показать настройки (Ctrl+Alt+)"), ("Hot.Reconnect", "Переподключить (Ctrl+Alt+)"),
                ("Hot.Exit", "Выход (Ctrl+Alt+)"), ("Hot.Lock", "Блокировка (Ctrl+Alt+)"),
                ("Sys.Title", "Система"), ("Sys.Style", "Тема"), ("Sys.StyleLight", "Светлая (по умолчанию)"), ("Sys.StyleDark", "Тёмная"),
                ("Sys.Language", "Язык"),
                ("Sys.StyleTip", "Цветовая тема интерфейса: светлая или тёмная."),
                ("Sys.LangTip", "Язык интерфейса. Непереведённый текст берётся из английского/китайского."),
                ("Sys.Applied", "Настройки применены; раскладка синхронизирована"),
                ("Opt.WrapMouse", "Циклическая мышь"), ("Opt.ShareClipboard", "Общий буфер обмена"),
                ("Opt.TransferFile", "Включить передачу файлов"), ("Opt.HideMouse", "Скрывать локальный курсор"),
                ("Opt.DrawCursor", "Рисовать курсор (резерв)"), ("Opt.SameSubnet", "Только одна подсеть"),
                ("Opt.ShowStatus", "Показывать статус"), ("Opt.AutoStart", "Автозапуск и подключение"),
                ("Opt.BlockSaver", "Блокировать заставку"), ("Opt.MoveRel", "Относительное движение мыши"),
                ("Opt.BlockCorner", "Не переключать в углах"), ("Opt.EasyMouse", "Easy Mouse (край экрана)"),
                ("Tip.WrapMouse", "Соединяет левый/правый (или верхний/нижний) края. Переключение только к занятому соседу."),
                ("Tip.ShareClipboard", "Общий буфер обмена между подключёнными ПК."),
                ("Tip.TransferFile", "Разрешает передачу файлов по запросу и прямую отправку."),
                ("Tip.HideMouse", "Показывает только курсор активного ПК. Скрывает локальный при удалённом управлении."),
                ("Tip.DrawCursor", "Резерв: рисовать виртуальный курсор. Пока не реализовано."),
                ("Tip.SameSubnet", "Разрешает только узлы в той же подсети."),
                ("Tip.ShowStatus", "Показывает состояние внизу окна."),
                ("Tip.AutoStart", "Запуск ~через 15 с после входа через Планировщик заданий."),
                ("Tip.BlockSaver", "Предотвращает заставку на других ПК при удалённом управлении."),
                ("Tip.MoveRel", "Передаёт относительные движения мыши. Для рабочего стола лучше выключить."),
                ("Tip.BlockCorner", "Нет переключения возле углов экрана."),
                ("Tip.EasyMouse", "Выход за край экрана переключает на соседний ПК.")),
            ["ar-SA"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "أساسي"), ("Tab.Options", "متقدم"), ("Tab.Network", "الشبكة"), ("Tab.Transfer", "نقل الملفات"),
                ("Btn.Apply", "تطبيق"), ("Btn.HideTray", "إخفاء إلى الشريط"), ("Btn.Exit", "خروج"),
                ("Btn.About", "حول"), ("Btn.Help", "؟ مساعدة"),
                ("Feat.Title", "الميزات"), ("Beh.Title", "السلوك"), ("Hot.Title", "اختصارات لوحة المفاتيح"),
                ("Hot.Switch", "تبديل الجهاز (Ctrl+Alt+)"), ("Hot.Off", "إيقاف"),
                ("Sys.Title", "النظام"), ("Sys.Style", "المظهر"), ("Sys.StyleLight", "فاتح (افتراضي)"), ("Sys.StyleDark", "داكن"),
                ("Sys.Language", "اللغة"), ("Sys.Applied", "تم تطبيق الإعدادات ومزامنة التخطيط"),
                ("Opt.WrapMouse", "التفاف المؤشر"), ("Opt.ShareClipboard", "مشاركة الحافظة"),
                ("Opt.TransferFile", "تمكين نقل الملفات"), ("Opt.EasyMouse", "ماوس سهل (الحافة للتبديل)")),
            ["hi-IN"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "मूल"), ("Tab.Options", "उन्नत"), ("Tab.Network", "नेटवर्क"), ("Tab.Transfer", "स्थानांतरण"),
                ("Btn.Apply", "लागू करें"), ("Btn.HideTray", "ट्रે में छिपाएँ"), ("Btn.Exit", "बाहर"),
                ("Btn.About", "परिचय"), ("Btn.Help", "? सहायता"),
                ("Feat.Title", "सुविधाएँ"), ("Beh.Title", "व्यवहार"), ("Hot.Title", "कीबोर्ड शॉर्टकट"),
                ("Sys.Title", "सिस्टम"), ("Sys.Style", "थीम"), ("Sys.StyleLight", "हल्की (डिफ़ॉल्ट)"), ("Sys.StyleDark", "गहरी"),
                ("Sys.Language", "भाषा"), ("Sys.Applied", "सेटिंग लागू; लेआउट सिंक"),
                ("Opt.ShareClipboard", "क्लिपबोर्ड साझा करें"), ("Opt.TransferFile", "फ़ाइल स्थानांतरण सक्षम"),
                ("Opt.EasyMouse", "ईज़ी माउस (किनारे से स्विच)")),
            ["th-TH"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "พื้นฐาน"), ("Tab.Options", "ขั้นสูง"), ("Tab.Network", "เครือข่าย"), ("Tab.Transfer", "ถ่ายโอน"),
                ("Btn.Apply", "ใช้"), ("Btn.HideTray", "ซ่อนไปถาด"), ("Btn.Exit", "ออก"),
                ("Btn.About", "เกี่ยวกับ"), ("Btn.Help", "? ช่วยเหลือ"),
                ("Feat.Title", "ฟีเจอร์"), ("Beh.Title", "พฤติกรรม"), ("Hot.Title", "ปุ่มลัด"),
                ("Sys.Title", "ระบบ"), ("Sys.Style", "ธีม"), ("Sys.StyleLight", "สว่าง (ค่าเริ่มต้น)"), ("Sys.StyleDark", "มืด"),
                ("Sys.Language", "ภาษา"), ("Sys.Applied", "ใช้การตั้งค่าแล้ว และซิงค์เลย์เอาต์"),
                ("Opt.ShareClipboard", "แชร์คลิปบอร์ด"), ("Opt.TransferFile", "เปิดการถ่ายโอนไฟล์"),
                ("Opt.EasyMouse", "Easy Mouse (ขอบจอเพื่อสลับ)")),
            ["vi-VN"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Cơ bản"), ("Tab.Options", "Nâng cao"), ("Tab.Network", "Mạng"), ("Tab.Transfer", "Truyền tệp"),
                ("Btn.Apply", "Áp dụng"), ("Btn.HideTray", "Ẩn xuống khay"), ("Btn.Exit", "Thoát"),
                ("Btn.About", "Giới thiệu"), ("Btn.Help", "? Trợ giúp"),
                ("Feat.Title", "Tính năng"), ("Beh.Title", "Hành vi"), ("Hot.Title", "Phím tắt"),
                ("Sys.Title", "Hệ thống"), ("Sys.Style", "Giao diện"), ("Sys.StyleLight", "Sáng (mặc định)"), ("Sys.StyleDark", "Tối"),
                ("Sys.Language", "Ngôn ngữ"), ("Sys.Applied", "Đã áp dụng; bố cục đã đồng bộ"),
                ("Opt.ShareClipboard", "Chia sẻ clipboard"), ("Opt.TransferFile", "Bật truyền tệp"),
                ("Opt.EasyMouse", "Easy Mouse (mép để chuyển)")),
            ["id-ID"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Dasar"), ("Tab.Options", "Lanjutan"), ("Tab.Network", "Jaringan"), ("Tab.Transfer", "Transfer"),
                ("Btn.Apply", "Terapkan"), ("Btn.HideTray", "Sembunyikan ke baki"), ("Btn.Exit", "Keluar"),
                ("Btn.About", "Tentang"), ("Btn.Help", "? Bantuan"),
                ("Feat.Title", "Fitur"), ("Beh.Title", "Perilaku"), ("Hot.Title", "Pintasan keyboard"),
                ("Sys.Title", "Sistem"), ("Sys.Style", "Tema"), ("Sys.StyleLight", "Terang (default)"), ("Sys.StyleDark", "Gelap"),
                ("Sys.Language", "Bahasa"), ("Sys.Applied", "Pengaturan diterapkan; tata letak disinkronkan"),
                ("Opt.ShareClipboard", "Bagikan papan klip"), ("Opt.TransferFile", "Aktifkan transfer file"),
                ("Opt.EasyMouse", "Easy Mouse (tepi untuk beralih)")),
            ["tr-TR"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Temel"), ("Tab.Options", "Gelişmiş"), ("Tab.Network", "Ağ"), ("Tab.Transfer", "Aktarım"),
                ("Btn.Apply", "Uygula"), ("Btn.HideTray", "Tepsiye gizle"), ("Btn.Exit", "Çıkış"),
                ("Btn.About", "Hakkında"), ("Btn.Help", "? Yardım"),
                ("Feat.Title", "Özellikler"), ("Beh.Title", "Davranış"), ("Hot.Title", "Klavye kısayolları"),
                ("Sys.Title", "Sistem"), ("Sys.Style", "Tema"), ("Sys.StyleLight", "Açık (varsayılan)"), ("Sys.StyleDark", "Koyu"),
                ("Sys.Language", "Dil"), ("Sys.Applied", "Ayarlar uygulandı; düzen senkronize edildi"),
                ("Opt.ShareClipboard", "Panoyu paylaş"), ("Opt.TransferFile", "Dosya aktarımını etkinleştir"),
                ("Opt.EasyMouse", "Easy Mouse (kenardan geçiş)")),
            ["it-IT"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Base"), ("Tab.Options", "Avanzate"), ("Tab.Network", "Rete"), ("Tab.Transfer", "Trasferimenti"),
                ("Btn.Apply", "Applica"), ("Btn.HideTray", "Nascondi nella tray"), ("Btn.Exit", "Esci"),
                ("Btn.About", "Informazioni"), ("Btn.Help", "? Aiuto"),
                ("Feat.Title", "Funzioni"), ("Beh.Title", "Comportamento"), ("Hot.Title", "Scorciatoie"),
                ("Sys.Title", "Sistema"), ("Sys.Style", "Tema"), ("Sys.StyleLight", "Chiaro (predefinito)"), ("Sys.StyleDark", "Scuro"),
                ("Sys.Language", "Lingua"), ("Sys.Applied", "Impostazioni applicate; layout sincronizzato"),
                ("Opt.ShareClipboard", "Condividi appunti"), ("Opt.TransferFile", "Abilita trasferimento file"),
                ("Opt.EasyMouse", "Easy Mouse (bordo per cambiare)")),
            ["nl-NL"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Basis"), ("Tab.Options", "Geavanceerd"), ("Tab.Network", "Netwerk"), ("Tab.Transfer", "Overdracht"),
                ("Btn.Apply", "Toepassen"), ("Btn.HideTray", "Naar systeemvak"), ("Btn.Exit", "Afsluiten"),
                ("Btn.About", "Info"), ("Btn.Help", "? Help"),
                ("Feat.Title", "Functies"), ("Beh.Title", "Gedrag"), ("Hot.Title", "Sneltoetsen"),
                ("Sys.Title", "Systeem"), ("Sys.Style", "Thema"), ("Sys.StyleLight", "Licht (standaard)"), ("Sys.StyleDark", "Donker"),
                ("Sys.Language", "Taal"), ("Sys.Applied", "Instellingen toegepast; indeling gesynchroniseerd"),
                ("Opt.ShareClipboard", "Klembord delen"), ("Opt.TransferFile", "Bestandsoverdracht inschakelen"),
                ("Opt.EasyMouse", "Easy Mouse (rand om te wisselen)")),
            ["pl-PL"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Podstawowe"), ("Tab.Options", "Zaawansowane"), ("Tab.Network", "Sieć"), ("Tab.Transfer", "Transfer"),
                ("Btn.Apply", "Zastosuj"), ("Btn.HideTray", "Ukryj w zasobniku"), ("Btn.Exit", "Wyjdź"),
                ("Btn.About", "O programie"), ("Btn.Help", "? Pomoc"),
                ("Feat.Title", "Funkcje"), ("Beh.Title", "Zachowanie"), ("Hot.Title", "Skróty klawiszowe"),
                ("Sys.Title", "System"), ("Sys.Style", "Motyw"), ("Sys.StyleLight", "Jasny (domyślny)"), ("Sys.StyleDark", "Ciemny"),
                ("Sys.Language", "Język"), ("Sys.Applied", "Ustawienia zastosowane; układ zsynchronizowany"),
                ("Opt.ShareClipboard", "Udostępniaj schowek"), ("Opt.TransferFile", "Włącz transfer plików"),
                ("Opt.EasyMouse", "Easy Mouse (krawędź do przełączania)")),
            ["uk-UA"] = LocCatalogs.GenericFromEn(en,
                ("Tab.Basic", "Основне"), ("Tab.Options", "Додатково"), ("Tab.Network", "Мережа"), ("Tab.Transfer", "Передача"),
                ("Btn.Apply", "Застосувати"), ("Btn.HideTray", "Сховати в трей"), ("Btn.Exit", "Вихід"),
                ("Btn.About", "Про програму"), ("Btn.Help", "? Довідка"),
                ("Feat.Title", "Функції"), ("Beh.Title", "Поведінка"), ("Hot.Title", "Гарячі клавіші"),
                ("Sys.Title", "Система"), ("Sys.Style", "Тема"), ("Sys.StyleLight", "Світла (типово)"), ("Sys.StyleDark", "Темна"),
                ("Sys.Language", "Мова"), ("Sys.Applied", "Параметри застосовано; макет синхронізовано"),
                ("Opt.ShareClipboard", "Спільний буфер"), ("Opt.TransferFile", "Увімкнути передачу файлів"),
                ("Opt.EasyMouse", "Easy Mouse (край для перемикання)")),
        };
    }

    private static Dictionary<string, string> Zh() => new(StringComparer.Ordinal)
    {
        ["Tab.Basic"] = "基础设置",
        ["Tab.Options"] = "高级选项",
        ["Tab.Network"] = "网络设置",
        ["Tab.Transfer"] = "文件传输",
        ["Btn.Apply"] = "应用",
        ["Btn.HideTray"] = "隐藏到托盘",
        ["Btn.Exit"] = "退出",
        ["Btn.About"] = "关于",
        ["Btn.Help"] = "？ 帮助",
        ["Feat.Title"] = "功能",
        ["Beh.Title"] = "行为",
        ["Hot.Title"] = "键盘快捷键",
        ["Hot.Switch"] = "切换机器（Ctrl+Alt+）",
        ["Hot.F14"] = "F1–F4",
        ["Hot.Num"] = "1–4",
        ["Hot.Off"] = "禁用",
        ["Hot.Settings"] = "显示设置 (Ctrl+Alt+)",
        ["Hot.Reconnect"] = "重新连接 (Ctrl+Alt+)",
        ["Hot.Exit"] = "退出程序 (Ctrl+Alt+)",
        ["Hot.Lock"] = "锁定机器 (Ctrl+Alt+)",
        ["Sys.Title"] = "系统设置",
        ["Sys.Style"] = "样式",
        ["Sys.StyleLight"] = "淡色（默认）",
        ["Sys.StyleDark"] = "深色",
        ["Sys.Language"] = "语言",
        ["Sys.StyleTip"] = "界面配色主题：淡色或深色，点击「应用」后立即生效。",
        ["Sys.LangTip"] = "界面显示语言。可切换万国语言；未完整翻译的词条会回退到英文或中文。",
        ["Sys.Applied"] = "设置已应用，布局已同步到其他电脑",
        ["Opt.WrapMouse"] = "环形鼠标（首尾相连）",
        ["Opt.ShareClipboard"] = "共享剪贴板",
        ["Opt.TransferFile"] = "启用文件传输",
        ["Opt.HideMouse"] = "控制远端时隐藏本机边缘光标",
        ["Opt.DrawCursor"] = "绘制鼠标光标（预留）",
        ["Opt.SameSubnet"] = "仅同网段连接",
        ["Opt.ShowStatus"] = "显示状态消息",
        ["Opt.AutoStart"] = "开机自动启动并连接",
        ["Opt.BlockSaver"] = "阻止其他机器屏保",
        ["Opt.MoveRel"] = "相对移动鼠标",
        ["Opt.BlockCorner"] = "屏幕四角阻挡切换",
        ["Opt.EasyMouse"] = "轻松鼠标（移出边缘切换）",
        ["Tip.WrapMouse"] = "布局最左与最右（或最上与最下）首尾相连：鼠标从一侧移出可绕到另一侧有电脑的槽位。仅相邻槽位有电脑时才会切换。",
        ["Tip.ShareClipboard"] = "在已连接电脑之间共享剪贴板。复制文本、图片等内容后，鼠标移到目标机再粘贴。",
        ["Tip.TransferFile"] = "允许通过剪贴板按需传输文件，以及「文件传输」页的直接推送。关闭后仍可共享文本/图片剪贴板。",
        ["Tip.HideMouse"] = "只显示当前操作电脑的光标；进入远端时隐藏本机光标。本机物理鼠标活动时立即显示本机并隐藏其它电脑光标。",
        ["Tip.DrawCursor"] = "预留选项：在远端屏幕上绘制虚拟光标。当前版本尚未启用实际绘制逻辑。",
        ["Tip.SameSubnet"] = "仅允许与本机同一网段的电脑建立连接，可减少误连，但跨网段（如 VPN）将无法连接。",
        ["Tip.ShowStatus"] = "在窗口底部显示连接、传输等状态提示信息。",
        ["Tip.AutoStart"] = "登录后约 15 秒以管理员权限启动（任务计划程序）。因需提权，不能用普通启动项。",
        ["Tip.BlockSaver"] = "当本机控制远端时，尝试阻止其它机器进入屏保，避免切换后无法操作。",
        ["Tip.MoveRel"] = "使用相对位移转发鼠标移动。部分游戏或远程桌面场景可能更流畅，一般办公建议关闭。",
        ["Tip.BlockCorner"] = "屏幕四角附近不触发边缘切换，减少误触（例如移向关闭按钮时）。",
        ["Tip.EasyMouse"] = "启用后，将鼠标移出屏幕边缘即可切换到布局中相邻的电脑。",
    };

    private static Dictionary<string, string> En() => new(StringComparer.Ordinal)
    {
        ["Tab.Basic"] = "Basic",
        ["Tab.Options"] = "Advanced",
        ["Tab.Network"] = "Network",
        ["Tab.Transfer"] = "Transfers",
        ["Btn.Apply"] = "Apply",
        ["Btn.HideTray"] = "Hide to tray",
        ["Btn.Exit"] = "Exit",
        ["Btn.About"] = "About",
        ["Btn.Help"] = "? Help",
        ["Feat.Title"] = "Features",
        ["Beh.Title"] = "Behavior",
        ["Hot.Title"] = "Keyboard shortcuts",
        ["Hot.Switch"] = "Switch machine (Ctrl+Alt+)",
        ["Hot.F14"] = "F1–F4",
        ["Hot.Num"] = "1–4",
        ["Hot.Off"] = "Off",
        ["Hot.Settings"] = "Show settings (Ctrl+Alt+)",
        ["Hot.Reconnect"] = "Reconnect (Ctrl+Alt+)",
        ["Hot.Exit"] = "Exit (Ctrl+Alt+)",
        ["Hot.Lock"] = "Lock machine (Ctrl+Alt+)",
        ["Sys.Title"] = "System",
        ["Sys.Style"] = "Theme",
        ["Sys.StyleLight"] = "Light (default)",
        ["Sys.StyleDark"] = "Dark",
        ["Sys.Language"] = "Language",
        ["Sys.StyleTip"] = "UI color theme: light or dark. Click Apply to take effect.",
        ["Sys.LangTip"] = "UI language. Untranslated strings fall back to English/Chinese.",
        ["Sys.Applied"] = "Settings applied; layout synced to peers.",
        ["Opt.WrapMouse"] = "Wrap mouse (loop edges)",
        ["Opt.ShareClipboard"] = "Share clipboard",
        ["Opt.TransferFile"] = "Enable file transfer",
        ["Opt.HideMouse"] = "Hide local cursor when controlling remote",
        ["Opt.DrawCursor"] = "Draw mouse cursor (reserved)",
        ["Opt.SameSubnet"] = "Same subnet only",
        ["Opt.ShowStatus"] = "Show status messages",
        ["Opt.AutoStart"] = "Start with Windows and connect",
        ["Opt.BlockSaver"] = "Block screen saver on other PCs",
        ["Opt.MoveRel"] = "Move mouse relatively",
        ["Opt.BlockCorner"] = "Block switching at corners",
        ["Opt.EasyMouse"] = "Easy Mouse (edge to switch)",
        ["Tip.WrapMouse"] = "Connect leftmost/rightmost (or top/bottom) slots in a loop. Switching only happens when the neighboring slot has a PC.",
        ["Tip.ShareClipboard"] = "Share clipboard across connected PCs. Copy text/images, move to the target PC, then paste.",
        ["Tip.TransferFile"] = "Allow on-demand file transfer via clipboard and Direct Transfer. Text/images still work if disabled.",
        ["Tip.HideMouse"] = "Show only the active PC cursor. Hide local cursor when controlling a remote PC; physical local input restores local cursor.",
        ["Tip.DrawCursor"] = "Reserved: draw a virtual cursor on the remote screen. Not implemented yet.",
        ["Tip.SameSubnet"] = "Only allow peers on the same subnet. Blocks cross-subnet/VPN connections.",
        ["Tip.ShowStatus"] = "Show connection and transfer status at the bottom of the window.",
        ["Tip.AutoStart"] = "Starts ~15s after logon via Task Scheduler (highest privileges). Registry Run is unreliable with UAC.",
        ["Tip.BlockSaver"] = "While controlling a remote PC, try to prevent other machines from entering screensaver.",
        ["Tip.MoveRel"] = "Forward mouse moves as relative deltas. May help some games; leave off for normal desktop use.",
        ["Tip.BlockCorner"] = "Do not switch when the pointer is near screen corners, reducing accidental switches.",
        ["Tip.EasyMouse"] = "Move the pointer off a screen edge to switch to the adjacent PC in the layout.",
    };
}

public sealed record LanguageOption(string Code, string NativeName, string EnglishName)
{
    public string Display => NativeName;
    public override string ToString() => NativeName;
}
