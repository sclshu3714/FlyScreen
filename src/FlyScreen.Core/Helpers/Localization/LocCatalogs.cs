namespace FlyScreen.Helpers.Localization;

/// <summary>各语言完整词条（与 Loc 中文键一致）。</summary>
internal static class LocCatalogs
{
    public static Dictionary<string, string> Clone(Dictionary<string, string> src)
        => new(src, StringComparer.Ordinal);

    public static void Overlay(Dictionary<string, string> target, params (string Key, string Value)[] pairs)
    {
        foreach (var (k, v) in pairs)
            target[k] = v;
    }

    public static Dictionary<string, string> ZhTw(Dictionary<string, string> zh)
    {
        var t = Clone(zh);
        Overlay(t,
            ("Tab.Basic", "基礎設置"), ("Tab.Options", "高級選項"), ("Tab.Network", "網絡設置"), ("Tab.Transfer", "文件傳輸"),
            ("Btn.Apply", "應用"), ("Btn.HideTray", "隱藏到托盤"), ("Btn.Exit", "退出"), ("Btn.About", "關於"), ("Btn.Help", "？ 幫助"),
            ("Feat.Title", "功能"), ("Beh.Title", "行為"), ("Hot.Title", "鍵盤快捷鍵"),
            ("Hot.Switch", "切換機器（Ctrl+Alt+）"), ("Hot.Off", "禁用"),
            ("Hot.Settings", "顯示設置 (Ctrl+Alt+)"), ("Hot.Reconnect", "重新連接 (Ctrl+Alt+)"),
            ("Hot.Exit", "退出程式 (Ctrl+Alt+)"), ("Hot.Lock", "鎖定機器 (Ctrl+Alt+)"),
            ("Sys.Title", "系統設置"), ("Sys.Style", "樣式"), ("Sys.StyleLight", "淡色（預設）"), ("Sys.StyleDark", "深色"),
            ("Sys.Language", "語言"),
            ("Sys.StyleTip", "介面配色主題：淡色或深色。"),
            ("Sys.LangTip", "介面顯示語言。未完整翻譯時會回退到英文或中文。"),
            ("Sys.Applied", "設定已套用，版面已同步到其他電腦"),
            ("Opt.WrapMouse", "環形滑鼠（首尾相連）"), ("Opt.ShareClipboard", "共用剪貼簿"),
            ("Opt.TransferFile", "啟用檔案傳輸"), ("Opt.HideMouse", "隱藏游標"),
            ("Opt.DrawCursor", "繪製滑鼠游標（預留）"), ("Opt.SameSubnet", "僅同網段連線"),
            ("Opt.ShowStatus", "顯示狀態訊息"), ("Opt.AutoStart", "開機自動啟動並連線"),
            ("Opt.BlockSaver", "阻止其他機器螢幕保護"), ("Opt.MoveRel", "相對移動滑鼠"),
            ("Opt.BlockCorner", "螢幕四角阻擋切換"), ("Opt.EasyMouse", "輕鬆滑鼠（移出邊緣切換）"),
            ("Tip.WrapMouse", "版面最左與最右（或最上與最下）首尾相連：滑鼠從一側移出可繞到另一側有電腦的槽位。僅相鄰槽位有電腦時才會切換。"),
            ("Tip.ShareClipboard", "在已連線電腦之間共用剪貼簿。複製文字、圖片等內容後，滑鼠移到目標機再貼上。"),
            ("Tip.TransferFile", "允許透過剪貼簿按需傳輸檔案，以及「檔案傳輸」頁的直接推送。關閉後仍可共用文字/圖片剪貼簿。"),
            ("Tip.HideMouse", "啟用後同一時間僅活動電腦顯示游標，其餘隱藏；實體滑鼠活動即奪回活動權。關閉後非活動電腦游標置中顯示。"),
            ("Tip.DrawCursor", "預留選項：在遠端螢幕上繪製虛擬游標。目前版本尚未啟用實際繪製邏輯。"),
            ("Tip.SameSubnet", "僅允許與本機同一網段的電腦建立連線，可減少誤連，但跨網段（如 VPN）將無法連線。"),
            ("Tip.ShowStatus", "在視窗底部顯示連線、傳輸等狀態提示訊息。"),
            ("Tip.AutoStart", "登入後約 15 秒以系統管理員權限啟動（工作排程器）。因需提權，不能用普通啟動項。"),
            ("Tip.BlockSaver", "當本機控制遠端時，嘗試阻止其他機器進入螢幕保護，避免切換後無法操作。"),
            ("Tip.MoveRel", "使用相對位移轉發滑鼠移動。部分遊戲或遠端桌面場景可能更流暢，一般辦公建議關閉。"),
            ("Tip.BlockCorner", "螢幕四角附近不觸發邊緣切換，減少誤觸（例如移向關閉按鈕時）。"),
            ("Tip.EasyMouse", "啟用後，將滑鼠移出螢幕邊緣即可切換到版面中相鄰的電腦。"));
        return t;
    }

    public static Dictionary<string, string> Ja(Dictionary<string, string> en)
    {
        var t = Clone(en);
        Overlay(t,
            ("Tab.Basic", "基本設定"), ("Tab.Options", "詳細設定"), ("Tab.Network", "ネットワーク"), ("Tab.Transfer", "ファイル転送"),
            ("Btn.Apply", "適用"), ("Btn.HideTray", "トレイに隠す"), ("Btn.Exit", "終了"), ("Btn.About", "情報"), ("Btn.Help", "？ ヘルプ"),
            ("Feat.Title", "機能"), ("Beh.Title", "動作"), ("Hot.Title", "キーボードショートカット"),
            ("Hot.Switch", "PC切替（Ctrl+Alt+）"), ("Hot.Off", "無効"),
            ("Hot.Settings", "設定を表示 (Ctrl+Alt+)"), ("Hot.Reconnect", "再接続 (Ctrl+Alt+)"),
            ("Hot.Exit", "終了 (Ctrl+Alt+)"), ("Hot.Lock", "PCをロック (Ctrl+Alt+)"),
            ("Sys.Title", "システム"), ("Sys.Style", "テーマ"), ("Sys.StyleLight", "ライト（既定）"), ("Sys.StyleDark", "ダーク"),
            ("Sys.Language", "言語"),
            ("Sys.StyleTip", "UI の配色テーマ：ライトまたはダーク。"),
            ("Sys.LangTip", "表示言語。未翻訳の文言は英語／中国語にフォールバックします。"),
            ("Sys.Applied", "設定を適用し、レイアウトを他のPCへ同期しました"),
            ("Opt.WrapMouse", "マウス折り返し（端を連結）"), ("Opt.ShareClipboard", "クリップボード共有"),
            ("Opt.TransferFile", "ファイル転送を有効化"), ("Opt.HideMouse", "カーソルを隠す"),
            ("Opt.DrawCursor", "マウスカーソル描画（予約）"), ("Opt.SameSubnet", "同一サブネットのみ"),
            ("Opt.ShowStatus", "ステータスメッセージを表示"), ("Opt.AutoStart", "起動時に自動開始して接続"),
            ("Opt.BlockSaver", "他PCのスクリーンセーバーを抑制"), ("Opt.MoveRel", "相対移動でマウスを送る"),
            ("Opt.BlockCorner", "四隅では切替しない"), ("Opt.EasyMouse", "イージーマウス（端で切替）"),
            ("Tip.WrapMouse", "レイアウトの左右（または上下）端を連結します。隣のスロットにPCがある場合のみ切り替わります。"),
            ("Tip.ShareClipboard", "接続中のPC間でクリップボードを共有します。コピー後、対象PCへ移動して貼り付けます。"),
            ("Tip.TransferFile", "クリップボード経由のオンデマンド転送と直接転送を許可します。オフでも文字／画像は共有できます。"),
            ("Tip.HideMouse", "操作中のPCのカーソルだけを表示します。遠隔操作時は自機カーソルを隠します。"),
            ("Tip.DrawCursor", "予約：遠隔画面に仮想カーソルを描画。未実装です。"),
            ("Tip.SameSubnet", "同一サブネットの相手のみ接続を許可します。VPNなど跨ぎは拒否されます。"),
            ("Tip.ShowStatus", "ウィンドウ下部に接続や転送の状態を表示します。"),
            ("Tip.AutoStart", "ログオン約15秒後にタスクスケジューラ（最高権限）で起動します。"),
            ("Tip.BlockSaver", "遠隔操作中、他マシンのスクリーンセーバーを抑止します。"),
            ("Tip.MoveRel", "マウス移動を相対量で転送します。通常はオフ推奨です。"),
            ("Tip.BlockCorner", "画面四隅付近では端切替を行いません。"),
            ("Tip.EasyMouse", "ポインタを画面端から出すと隣接PCへ切り替えます。"));
        return t;
    }

    public static Dictionary<string, string> Ko(Dictionary<string, string> en)
    {
        var t = Clone(en);
        Overlay(t,
            ("Tab.Basic", "기본 설정"), ("Tab.Options", "고급 옵션"), ("Tab.Network", "네트워크"), ("Tab.Transfer", "파일 전송"),
            ("Btn.Apply", "적용"), ("Btn.HideTray", "트레이로 숨기기"), ("Btn.Exit", "종료"), ("Btn.About", "정보"), ("Btn.Help", "? 도움말"),
            ("Feat.Title", "기능"), ("Beh.Title", "동작"), ("Hot.Title", "키보드 단축키"),
            ("Hot.Switch", "PC 전환(Ctrl+Alt+)"), ("Hot.Off", "사용 안 함"),
            ("Hot.Settings", "설정 표시 (Ctrl+Alt+)"), ("Hot.Reconnect", "다시 연결 (Ctrl+Alt+)"),
            ("Hot.Exit", "종료 (Ctrl+Alt+)"), ("Hot.Lock", "PC 잠금 (Ctrl+Alt+)"),
            ("Sys.Title", "시스템"), ("Sys.Style", "테마"), ("Sys.StyleLight", "밝은 테마(기본)"), ("Sys.StyleDark", "어두운 테마"),
            ("Sys.Language", "언어"),
            ("Sys.StyleTip", "UI 색상 테마: 밝은 모드 또는 어두운 모드."),
            ("Sys.LangTip", "표시 언어. 번역이 없으면 영어/중국어로 대체됩니다."),
            ("Sys.Applied", "설정을 적용하고 레이아웃을 다른 PC에 동기화했습니다"),
            ("Opt.WrapMouse", "마우스 순환(가장자리 연결)"), ("Opt.ShareClipboard", "클립보드 공유"),
            ("Opt.TransferFile", "파일 전송 사용"), ("Opt.HideMouse", "커서 숨기기"),
            ("Opt.DrawCursor", "마우스 커서 그리기(예약)"), ("Opt.SameSubnet", "같은 서브넷만 연결"),
            ("Opt.ShowStatus", "상태 메시지 표시"), ("Opt.AutoStart", "시작 시 자동 실행 및 연결"),
            ("Opt.BlockSaver", "다른 PC 화면 보호기 차단"), ("Opt.MoveRel", "상대 이동으로 마우스 전송"),
            ("Opt.BlockCorner", "모서리에서 전환 차단"), ("Opt.EasyMouse", "이지 마우스(가장자리에서 전환)"),
            ("Tip.WrapMouse", "레이아웃 좌우(또는 상하) 끝을 연결합니다. 인접 슬롯에 PC가 있을 때만 전환됩니다."),
            ("Tip.ShareClipboard", "연결된 PC 간에 클립보드를 공유합니다. 복사 후 대상 PC로 이동해 붙여넣습니다."),
            ("Tip.TransferFile", "클립보드 온디맨드 전송과 직접 전송을 허용합니다. 꺼도 텍스트/이미지는 공유됩니다."),
            ("Tip.HideMouse", "현재 조작 중인 PC 커서만 표시합니다. 원격 제어 시 로컬 커서를 숨깁니다."),
            ("Tip.DrawCursor", "예약: 원격 화면에 가상 커서를 그립니다. 아직 구현되지 않았습니다."),
            ("Tip.SameSubnet", "같은 서브넷의 PC만 연결을 허용합니다. VPN 등 다른 망은 거부됩니다."),
            ("Tip.ShowStatus", "창 하단에 연결/전송 상태를 표시합니다."),
            ("Tip.AutoStart", "로그온 약 15초 후 작업 스케줄러(최고 권한)로 시작합니다."),
            ("Tip.BlockSaver", "원격 제어 중 다른 PC의 화면 보호기를 막습니다."),
            ("Tip.MoveRel", "마우스 이동을 상대 값으로 전달합니다. 일반 사용은 끄는 것을 권장합니다."),
            ("Tip.BlockCorner", "화면 모서리 근처에서는 가장자리 전환을 하지 않습니다."),
            ("Tip.EasyMouse", "포인터를 화면 밖으로 내보내면 인접 PC로 전환합니다."));
        return t;
    }

    public static Dictionary<string, string> Fr(Dictionary<string, string> en)
    {
        var t = Clone(en);
        Overlay(t,
            ("Tab.Basic", "Base"), ("Tab.Options", "Avancé"), ("Tab.Network", "Réseau"), ("Tab.Transfer", "Transferts"),
            ("Btn.Apply", "Appliquer"), ("Btn.HideTray", "Réduire dans la barre"), ("Btn.Exit", "Quitter"),
            ("Btn.About", "À propos"), ("Btn.Help", "? Aide"),
            ("Feat.Title", "Fonctions"), ("Beh.Title", "Comportement"), ("Hot.Title", "Raccourcis clavier"),
            ("Hot.Switch", "Changer de PC (Ctrl+Alt+)"), ("Hot.Off", "Désactivé"),
            ("Hot.Settings", "Afficher les réglages (Ctrl+Alt+)"), ("Hot.Reconnect", "Reconnecter (Ctrl+Alt+)"),
            ("Hot.Exit", "Quitter (Ctrl+Alt+)"), ("Hot.Lock", "Verrouiller (Ctrl+Alt+)"),
            ("Sys.Title", "Système"), ("Sys.Style", "Thème"), ("Sys.StyleLight", "Clair (par défaut)"), ("Sys.StyleDark", "Sombre"),
            ("Sys.Language", "Langue"),
            ("Sys.StyleTip", "Thème de couleurs de l’interface : clair ou sombre."),
            ("Sys.LangTip", "Langue de l’interface. Les textes non traduits basculent en anglais/chinois."),
            ("Sys.Applied", "Paramètres appliqués ; disposition synchronisée"),
            ("Opt.WrapMouse", "Souris circulaire"), ("Opt.ShareClipboard", "Partager le presse-papiers"),
            ("Opt.TransferFile", "Activer le transfert de fichiers"), ("Opt.HideMouse", "Masquer le curseur"),
            ("Opt.DrawCursor", "Dessiner le curseur (réservé)"), ("Opt.SameSubnet", "Même sous-réseau uniquement"),
            ("Opt.ShowStatus", "Afficher les messages d’état"), ("Opt.AutoStart", "Démarrer avec Windows et se connecter"),
            ("Opt.BlockSaver", "Bloquer l’économiseur d’écran"), ("Opt.MoveRel", "Déplacer la souris en relatif"),
            ("Opt.BlockCorner", "Bloquer le changement dans les coins"), ("Opt.EasyMouse", "Easy Mouse (bord pour changer)"),
            ("Tip.WrapMouse", "Relie les bords gauche/droit (ou haut/bas). Le changement n’a lieu que si le voisin a un PC."),
            ("Tip.ShareClipboard", "Partage le presse-papiers entre PC connectés. Copiez, allez sur la cible, puis collez."),
            ("Tip.TransferFile", "Autorise le transfert de fichiers à la demande et le transfert direct."),
            ("Tip.HideMouse", "N’affiche que le curseur du PC actif. Masque le curseur local en contrôle distant."),
            ("Tip.DrawCursor", "Réservé : dessiner un curseur virtuel. Non implémenté."),
            ("Tip.SameSubnet", "N’autorise que les pairs du même sous-réseau."),
            ("Tip.ShowStatus", "Affiche l’état de connexion/transfert en bas de la fenêtre."),
            ("Tip.AutoStart", "Démarre ~15 s après la connexion via le Planificateur de tâches."),
            ("Tip.BlockSaver", "Empêche l’économiseur d’écran des autres PC pendant le contrôle distant."),
            ("Tip.MoveRel", "Transmet les mouvements relatifs. Déconseillé pour un usage bureau normal."),
            ("Tip.BlockCorner", "Pas de bascule près des coins de l’écran."),
            ("Tip.EasyMouse", "Sortir le pointeur du bord change de PC voisin."));
        return t;
    }

    public static Dictionary<string, string> De(Dictionary<string, string> en)
    {
        var t = Clone(en);
        Overlay(t,
            ("Tab.Basic", "Basis"), ("Tab.Options", "Erweitert"), ("Tab.Network", "Netzwerk"), ("Tab.Transfer", "Übertragung"),
            ("Btn.Apply", "Übernehmen"), ("Btn.HideTray", "In Tray verbergen"), ("Btn.Exit", "Beenden"),
            ("Btn.About", "Info"), ("Btn.Help", "? Hilfe"),
            ("Feat.Title", "Funktionen"), ("Beh.Title", "Verhalten"), ("Hot.Title", "Tastenkürzel"),
            ("Hot.Switch", "PC wechseln (Ctrl+Alt+)"), ("Hot.Off", "Aus"),
            ("Hot.Settings", "Einstellungen (Ctrl+Alt+)"), ("Hot.Reconnect", "Neu verbinden (Ctrl+Alt+)"),
            ("Hot.Exit", "Beenden (Ctrl+Alt+)"), ("Hot.Lock", "PC sperren (Ctrl+Alt+)"),
            ("Sys.Title", "System"), ("Sys.Style", "Design"), ("Sys.StyleLight", "Hell (Standard)"), ("Sys.StyleDark", "Dunkel"),
            ("Sys.Language", "Sprache"),
            ("Sys.StyleTip", "Farbschema der Oberfläche: hell oder dunkel."),
            ("Sys.LangTip", "Anzeigesprache. Fehlende Übersetzungen fallen auf Englisch/Chinesisch zurück."),
            ("Sys.Applied", "Einstellungen übernommen; Layout synchronisiert"),
            ("Opt.WrapMouse", "Maus umlaufen"), ("Opt.ShareClipboard", "Zwischenablage teilen"),
            ("Opt.TransferFile", "Dateiübertragung aktivieren"), ("Opt.HideMouse", "Cursor ausblenden"),
            ("Opt.DrawCursor", "Mauszeiger zeichnen (Reserviert)"), ("Opt.SameSubnet", "Nur gleiches Subnetz"),
            ("Opt.ShowStatus", "Statusmeldungen anzeigen"), ("Opt.AutoStart", "Mit Windows starten und verbinden"),
            ("Opt.BlockSaver", "Bildschirmschoner blockieren"), ("Opt.MoveRel", "Maus relativ bewegen"),
            ("Opt.BlockCorner", "Ecken nicht umschalten"), ("Opt.EasyMouse", "Easy Mouse (Rand wechseln)"),
            ("Tip.WrapMouse", "Verbindet linke/rechte (oder obere/untere) Ränder. Umschalten nur bei belegt Nachbarplatz."),
            ("Tip.ShareClipboard", "Teilt die Zwischenablage zwischen verbundenen PCs."),
            ("Tip.TransferFile", "Erlaubt Dateiübertragung per Zwischenablage und Direktversand."),
            ("Tip.HideMouse", "Zeigt nur den Cursor des aktiven PCs. Blendet den lokalen Cursor bei Fernsteuerung aus."),
            ("Tip.DrawCursor", "Reserviert: virtuellen Cursor zeichnen. Noch nicht umgesetzt."),
            ("Tip.SameSubnet", "Erlaubt nur Peers im gleichen Subnetz."),
            ("Tip.ShowStatus", "Zeigt Verbindungs-/Übertragungsstatus unten an."),
            ("Tip.AutoStart", "Startet ca. 15 s nach Anmeldung über die Aufgabenplanung."),
            ("Tip.BlockSaver", "Verhindert Bildschirmschoner anderer PCs bei Fernsteuerung."),
            ("Tip.MoveRel", "Sendet relative Mausbewegungen. Für Desktop meist auslassen."),
            ("Tip.BlockCorner", "Kein Umschalten nahe den Bildschirmecken."),
            ("Tip.EasyMouse", "Maus über den Rand bewegen wechselt zum Nachbar-PC."));
        return t;
    }

    public static Dictionary<string, string> Es(Dictionary<string, string> en)
    {
        var t = Clone(en);
        Overlay(t,
            ("Tab.Basic", "Básico"), ("Tab.Options", "Avanzado"), ("Tab.Network", "Red"), ("Tab.Transfer", "Transferencias"),
            ("Btn.Apply", "Aplicar"), ("Btn.HideTray", "Ocultar en bandeja"), ("Btn.Exit", "Salir"),
            ("Btn.About", "Acerca de"), ("Btn.Help", "? Ayuda"),
            ("Feat.Title", "Funciones"), ("Beh.Title", "Comportamiento"), ("Hot.Title", "Atajos de teclado"),
            ("Hot.Switch", "Cambiar PC (Ctrl+Alt+)"), ("Hot.Off", "Desactivado"),
            ("Hot.Settings", "Mostrar ajustes (Ctrl+Alt+)"), ("Hot.Reconnect", "Reconectar (Ctrl+Alt+)"),
            ("Hot.Exit", "Salir (Ctrl+Alt+)"), ("Hot.Lock", "Bloquear PC (Ctrl+Alt+)"),
            ("Sys.Title", "Sistema"), ("Sys.Style", "Tema"), ("Sys.StyleLight", "Claro (predeterminado)"), ("Sys.StyleDark", "Oscuro"),
            ("Sys.Language", "Idioma"),
            ("Sys.StyleTip", "Tema de color de la interfaz: claro u oscuro."),
            ("Sys.LangTip", "Idioma de la interfaz. Sin traducción completa se usa inglés/chino."),
            ("Sys.Applied", "Ajustes aplicados; diseño sincronizado"),
            ("Opt.WrapMouse", "Ratón circular"), ("Opt.ShareClipboard", "Compartir portapapeles"),
            ("Opt.TransferFile", "Activar transferencia de archivos"), ("Opt.HideMouse", "Ocultar cursor"),
            ("Opt.DrawCursor", "Dibujar cursor (reservado)"), ("Opt.SameSubnet", "Solo misma subred"),
            ("Opt.ShowStatus", "Mostrar mensajes de estado"), ("Opt.AutoStart", "Iniciar con Windows y conectar"),
            ("Opt.BlockSaver", "Bloquear protector de pantalla"), ("Opt.MoveRel", "Mover ratón en relativo"),
            ("Opt.BlockCorner", "Bloquear cambio en esquinas"), ("Opt.EasyMouse", "Easy Mouse (borde para cambiar)"),
            ("Tip.WrapMouse", "Une los bordes izquierdo/derecho (o superior/inferior). Solo cambia si el vecino tiene PC."),
            ("Tip.ShareClipboard", "Comparte el portapapeles entre PCs conectados."),
            ("Tip.TransferFile", "Permite transferencia bajo demanda y envío directo de archivos."),
            ("Tip.HideMouse", "Muestra solo el cursor del PC activo. Oculta el local al controlar remoto."),
            ("Tip.DrawCursor", "Reservado: dibujar cursor virtual. Aún no implementado."),
            ("Tip.SameSubnet", "Solo permite pares de la misma subred."),
            ("Tip.ShowStatus", "Muestra el estado abajo en la ventana."),
            ("Tip.AutoStart", "Arranca ~15 s tras iniciar sesión con el Programador de tareas."),
            ("Tip.BlockSaver", "Evita el protector de pantalla en otros PCs al controlar remoto."),
            ("Tip.MoveRel", "Envía movimientos relativos. Mejor desactivado en escritorio."),
            ("Tip.BlockCorner", "No cambia cerca de las esquinas."),
            ("Tip.EasyMouse", "Salir por el borde cambia al PC adyacente."));
        return t;
    }

    public static Dictionary<string, string> GenericFromEn(Dictionary<string, string> en, params (string, string)[] pairs)
    {
        var t = Clone(en);
        Overlay(t, pairs);
        return t;
    }
}
