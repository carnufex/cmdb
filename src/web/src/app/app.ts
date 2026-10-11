import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from './auth/auth';
import { CurrentUser } from './shell/current-user';
import { PanelStack } from './shell/panels';
import { PanelHostComponent } from './shell/panel-host';
import { registerCoreCommands } from './shell/core-commands';
import { SearchComponent } from './shell/search';
import { ThemeStore } from './shell/theme';
import { Tools } from './shell/tools';
import { QueryPanelComponent } from './query/query-panel';
import { PerfPanelComponent } from './perf/perf-panel';
import { TreePanelComponent } from './tree/tree-panel';
import { ActivePlan } from './plans/active-plan';
import { PlanPanelComponent } from './plans/plan-panel';
import { GridPanelComponent } from './grid/grid-panel';
import { ChangelogPanelComponent, ChangelogStore } from './changelog/changelog';
import { VoiceCallComponent } from './voice/voice-call';
import { VoicePanelComponent } from './voice/voice-panel';
import { RackPanelComponent } from './objects/rack-panel';
import { FibrePanelComponent } from './objects/fibre-panel';
import { ServiceViewComponent } from './objects/service-view';
import { RoomPanelComponent } from './objects/room-panel';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    SearchComponent,
    PanelHostComponent,
    QueryPanelComponent,
    PerfPanelComponent,
    TreePanelComponent,
    PlanPanelComponent,
    GridPanelComponent,
    ChangelogPanelComponent,
    VoicePanelComponent,
    VoiceCallComponent,
    RackPanelComponent,
    FibrePanelComponent,
    ServiceViewComponent,
    RoomPanelComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  host: { '(document:keydown)': 'onKey($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly auth = inject(Auth);
  protected readonly theme = inject(ThemeStore);
  protected readonly panels = inject(PanelStack);
  protected readonly tools = inject(Tools);
  protected readonly plan = inject(ActivePlan);
  protected readonly changelog = inject(ChangelogStore);
  protected readonly me = inject(CurrentUser).me;

  constructor() {
    registerCoreCommands();
    void this.theme.load();
  }

  /** Alt+. steps the open tool through its sizes (#251). */
  protected onKey(event: KeyboardEvent): void {
    if (
      event.altKey &&
      !event.ctrlKey &&
      !event.metaKey &&
      (event.code === 'Period' || event.key === '.')
    ) {
      event.preventDefault();
      this.tools.cycleSize();
    }
  }
}
