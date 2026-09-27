import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Auth } from './auth/auth';

/** The user as the API sees them (GET /api/me). */
export interface Me {
  username: string;
  name: string | null;
  email: string | null;
  groups: string[];
}

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly auth = inject(Auth);
  protected readonly me = httpResource<Me>(() =>
    this.auth.isAuthenticated() ? '/api/me' : undefined,
  );
}
