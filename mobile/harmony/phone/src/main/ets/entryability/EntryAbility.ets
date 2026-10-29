import { AbilityConstant, UIAbility, Want } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { HarmonyAppHost, HarmonyHostMode } from 'common';

export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    // 强制移动端模式（即使用户在 2in1 上装了 phone 包）
    HarmonyAppHost.get(HarmonyHostMode.Mobile);
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    windowStage.loadContent('pages/Index');
  }
}
