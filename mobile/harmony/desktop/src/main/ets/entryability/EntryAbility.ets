import { AbilityConstant, UIAbility, Want } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { HarmonyAppHost, HarmonyHostMode } from 'common';

export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    // 鸿蒙电脑：Desktop Caps；若后续注入不可用可调用 degradeDesktopCaps()
    HarmonyAppHost.get(HarmonyHostMode.Desktop);
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    windowStage.loadContent('pages/Index');
  }
}
