/** JSON 字段名保持 PascalCase，与 .NET System.Text.Json 默认一致。 */

export class HandshakePayload {
  MachineName: string = '';
  SecurityKey: string = '';
  LocalIp: string = '';
  ProtocolVersion: number = 2;
  IsReply: boolean = false;
  Platform: string = PeerPlatform.HarmonyOS;
  DeviceClass: string = PeerDeviceClass.Phone;
  Caps: string[] = PeerCaps.HarmonyMobile;
  AppVersion: string = '1.6.0';
}

export class MouseMovePayload {
  Nx: number = 0;
  Ny: number = 0;
  Relative: boolean = false;
  Dx: number = 0;
  Dy: number = 0;
}

export class MouseButtonPayload {
  Button: number = 0;
  Down: boolean = false;
}

export class MouseWheelPayload {
  Delta: number = 0;
  Horizontal: boolean = false;
}

export class DiscoveryPacket {
  MachineName: string = '';
  Ip: string = '';
  TcpPort: number = 15155;
}

export class PeerCaps {
  static readonly EdgeMouse: string = 'EdgeMouse';
  static readonly AbsPointer: string = 'AbsPointer';
  static readonly ClipboardText: string = 'ClipboardText';
  static readonly ClipboardFiles: string = 'ClipboardFiles';
  static readonly DirectTransfer: string = 'DirectTransfer';
  static readonly LayoutSync: string = 'LayoutSync';
  static readonly InjectInput: string = 'InjectInput';
  static readonly CaptureInput: string = 'CaptureInput';

  static readonly HarmonyMobile: string[] = [
    PeerCaps.AbsPointer, PeerCaps.ClipboardText, PeerCaps.DirectTransfer
  ];

  static readonly HarmonyDesktop: string[] = [
    PeerCaps.EdgeMouse, PeerCaps.AbsPointer, PeerCaps.ClipboardText,
    PeerCaps.ClipboardFiles, PeerCaps.DirectTransfer, PeerCaps.LayoutSync,
    PeerCaps.InjectInput, PeerCaps.CaptureInput
  ];

  /** 鸿蒙电脑权限/API 不足时的降级 Caps。 */
  static readonly HarmonyDesktopDegraded: string[] = [
    PeerCaps.AbsPointer, PeerCaps.ClipboardText, PeerCaps.LayoutSync
  ];
}

export class PeerPlatform {
  static readonly HarmonyOS: string = 'HarmonyOS';
  static readonly Windows: string = 'Windows';
  static readonly Android: string = 'Android';
}

export class PeerDeviceClass {
  static readonly Desktop: string = 'Desktop';
  static readonly Phone: string = 'Phone';
  static readonly Tablet: string = 'Tablet';
  static readonly Unknown: string = 'Unknown';
}
