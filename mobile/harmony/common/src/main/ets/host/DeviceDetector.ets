import { deviceInfo } from '@kit.BasicServicesKit';
import { PeerDeviceClass } from '../protocol/Payloads';

export enum HarmonyHostMode {
  Mobile = 0,
  Desktop = 1
}

/**
 * 依据系统 deviceType 判断形态。
 * phone / wearable → Phone；tablet → Tablet；2in1 / pc / default → Desktop。
 */
export function detectDeviceClass(): string {
  const t = (deviceInfo.deviceType ?? '').toLowerCase();
  if (t === 'phone' || t === 'wearable') {
    return PeerDeviceClass.Phone;
  }
  if (t === 'tablet') {
    return PeerDeviceClass.Tablet;
  }
  if (t === '2in1' || t === 'pc' || t === 'default') {
    return PeerDeviceClass.Desktop;
  }
  return PeerDeviceClass.Unknown;
}

export function detectHostMode(): HarmonyHostMode {
  return detectDeviceClass() === PeerDeviceClass.Desktop
    ? HarmonyHostMode.Desktop
    : HarmonyHostMode.Mobile;
}

export function buildLocalMachineName(): string {
  const product = (deviceInfo.productModel ?? 'Harmony').replace(/[^A-Za-z0-9\-]/g, '-').substring(0, 24);
  const odid = (deviceInfo.ODID ?? '').substring(0, 4);
  if (odid.length > 0) {
    return `${product}-${odid}`;
  }
  return product.length > 0 ? product : 'Harmony-Device';
}
