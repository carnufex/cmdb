import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from './auth/auth';
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
import { VoicePanelComponent } from './voice/voice-panel';

/** The user as the API sees them (GET /api/me). */
export interface Me {
  username: string;
  name: string | null;
  email: string | null;
  groups: string[];
  /** Access scopes that apply (#22), by name. */
  scopes: string[] | null;
}

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
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly auth = inject(Auth);
  protected readonly theme = inject(ThemeStore);
  protected readonly panels = inject(PanelStack);
  protected readonly tools = inject(Tools);
  protected readonly plan = inject(ActivePlan);
  protected readonly changelog = inject(ChangelogStore);
  protected readonly me = httpResource<Me>(() =>
    this.auth.isAuthenticated() ? '/api/me' : undefined,
  );

  constructor() {
    registerCoreCommands();
    void this.theme.load();
  }
}
