import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Assistant } from './assistant';
import { parseAnswer } from './assistant-markdown';

/**
 * An answer from the model, drawn by this template from parsed data: no HTML from a model ever reaches the page. A citation
 * is a button that opens the step it comes from, and one that points at nothing is left out.
 */
@Component({
  selector: 'cmdb-assistant-answer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet],
  template: `
    <ng-template #inlines let-items>
      @for (item of items; track $index) {
        @switch (item.kind) {
          @case ('text') {
            <span class="text">{{ item.text }}</span>
          }
          @case ('strong') {
            <strong
              ><ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: item.children }"
            /></strong>
          }
          @case ('em') {
            <em
              ><ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: item.children }"
            /></em>
          }
          @case ('del') {
            <del
              ><ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: item.children }"
            /></del>
          }
          @case ('code') {
            <code>{{ item.text }}</code>
          }
          @case ('object') {
            <button type="button" class="object" (click)="assistant.openObject(item.ref)">
              <ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: item.children }"
              />
            </button>
          }
          @case ('cite') {
            @if (assistant.source(run(), item.source); as step) {
              <button
                type="button"
                class="cite"
                [title]="step.label"
                [attr.aria-label]="'Källa ' + item.source + ': ' + step.label"
                (click)="assistant.showSource(run(), item.source)"
              >
                {{ item.source }}
              </button>
            }
          }
        }
      }
    </ng-template>

    <ng-template #blockList let-items>
      @for (block of items; track $index) {
        @switch (block.kind) {
          @case ('p') {
            <p>
              <ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: block.inline }"
              />
            </p>
          }
          @case ('h') {
            <p class="heading">
              <ng-container
                [ngTemplateOutlet]="inlines"
                [ngTemplateOutletContext]="{ $implicit: block.inline }"
              />
            </p>
          }
          @case ('ul') {
            <ul>
              @for (entry of block.items; track $index) {
                <li>
                  <ng-container
                    [ngTemplateOutlet]="blockList"
                    [ngTemplateOutletContext]="{ $implicit: entry }"
                  />
                </li>
              }
            </ul>
          }
          @case ('ol') {
            <ol>
              @for (entry of block.items; track $index) {
                <li>
                  <ng-container
                    [ngTemplateOutlet]="blockList"
                    [ngTemplateOutletContext]="{ $implicit: entry }"
                  />
                </li>
              }
            </ol>
          }
          @case ('code') {
            <pre><code>{{ block.text }}</code></pre>
          }
          @case ('quote') {
            <blockquote>
              <ng-container
                [ngTemplateOutlet]="blockList"
                [ngTemplateOutletContext]="{ $implicit: block.blocks }"
              />
            </blockquote>
          }
          @case ('table') {
            <div class="table">
              <table>
                <thead>
                  <tr>
                    @for (cell of block.head; track $index) {
                      <th>
                        <ng-container
                          [ngTemplateOutlet]="inlines"
                          [ngTemplateOutletContext]="{ $implicit: cell }"
                        />
                      </th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (row of block.rows; track $index) {
                    <tr>
                      @for (cell of row; track $index) {
                        <td>
                          <ng-container
                            [ngTemplateOutlet]="inlines"
                            [ngTemplateOutletContext]="{ $implicit: cell }"
                          />
                        </td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          @case ('hr') {
            <hr />
          }
        }
      }
    </ng-template>

    <ng-container
      [ngTemplateOutlet]="blockList"
      [ngTemplateOutletContext]="{ $implicit: blocks() }"
    />
  `,
  styles: `
    :host {
      display: block;
      line-height: 1.5;
      overflow-wrap: anywhere;
    }
    p,
    ul,
    ol,
    pre,
    blockquote,
    .table {
      margin: 0 0 var(--space-2);
    }
    :host > :last-child,
    li > :last-child {
      margin-bottom: 0;
    }
    ul,
    ol {
      padding-left: var(--space-5);
    }
    li {
      margin-bottom: var(--space-1);
    }
    .text {
      white-space: pre-wrap;
    }
    .heading {
      margin-top: var(--space-3);
      font-weight: 600;
    }
    code {
      padding: 1px var(--space-1);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      font-family: var(--font-mono);
      font-size: var(--text-sm);
    }
    pre {
      padding: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      overflow-x: auto;
    }
    pre code {
      padding: 0;
      background: none;
    }
    blockquote {
      padding-left: var(--space-2);
      border-left: 2px solid var(--border-strong);
      color: var(--text-muted);
    }
    hr {
      border: 0;
      border-top: var(--line);
    }
    .table {
      overflow-x: auto;
    }
    table {
      border-collapse: collapse;
      font-size: var(--text-sm);
    }
    th,
    td {
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
      text-align: left;
    }
    th {
      font-weight: 600;
    }
    .object {
      padding: 0;
      border: 0;
      background: none;
      color: inherit;
      font: inherit;
      text-decoration: underline;
      text-decoration-color: var(--text-faint);
      cursor: pointer;
    }
    .object:hover {
      text-decoration-color: currentcolor;
    }
    .cite {
      min-width: 16px;
      height: 16px;
      margin: 0 1px;
      padding: 0 3px;
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      color: var(--text-muted);
      font-size: var(--text-xs);
      line-height: 14px;
      vertical-align: 1px;
      cursor: pointer;
    }
    .cite:hover {
      border-color: var(--border-strong);
      color: var(--text);
    }
  `,
})
export class AssistantAnswerComponent {
  protected readonly assistant = inject(Assistant);
  readonly text = input.required<string>();
  /** The question it answers: its citations count from there. */
  readonly run = input.required<number>();
  protected readonly blocks = computed(() =>
    parseAnswer(this.assistant.lexer(), this.text(), location.origin),
  );
}
