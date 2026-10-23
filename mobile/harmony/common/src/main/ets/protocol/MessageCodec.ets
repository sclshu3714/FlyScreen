import { util } from '@kit.ArkTS';

/**
 * 帧：1 byte type + 4 byte LE length + payload。
 */
export class MessageCodec {
  static readonly HEADER_SIZE: number = 5;
  static readonly MAX_PAYLOAD: number = 8 * 1024 * 1024;

  static encode(type: number, payload: Uint8Array = new Uint8Array(0)): Uint8Array {
    if (payload.length > MessageCodec.MAX_PAYLOAD) {
      throw new Error('payload too large');
    }
    const out = new Uint8Array(MessageCodec.HEADER_SIZE + payload.length);
    out[0] = type & 0xff;
    const len = payload.length;
    out[1] = len & 0xff;
    out[2] = (len >> 8) & 0xff;
    out[3] = (len >> 16) & 0xff;
    out[4] = (len >> 24) & 0xff;
    out.set(payload, MessageCodec.HEADER_SIZE);
    return out;
  }

  static encodeJson(type: number, obj: Object): Uint8Array {
    const text = JSON.stringify(obj);
    const encoder = new util.TextEncoder();
    return MessageCodec.encode(type, encoder.encodeInto(text));
  }

  static decodeJson<T>(payload: Uint8Array): T | null {
    try {
      const decoder = util.TextDecoder.create('utf-8');
      const text = decoder.decodeToString(payload);
      return JSON.parse(text) as T;
    } catch (_e) {
      return null;
    }
  }

  static encodeDiscoveryJson(obj: Object): Uint8Array {
    const encoder = new util.TextEncoder();
    return encoder.encodeInto(JSON.stringify(obj));
  }
}
