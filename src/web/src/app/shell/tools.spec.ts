import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { nextSize, preferredSize, Tools, toolSpecs } from './tools';

describe('tool sizes (#251)', () => {
  it('opens a tool in its own size unless another was picked for it on this device', () => {
    expect(preferredSize('plans', null)).toBe('half');
    expect(preferredSize('tree', null)).toBe('panel');
    expect(preferredSize('plans', { getItem: () => 'workspace' })).toBe('workspace');
    // A size the tool does not support, or broken storage, falls back to its own.
    expect(preferredSize('voice', { getItem: () => 'workspace' })).toBe('panel');
    expect(
      preferredSize('grid', {
        getItem: () => {
          throw new Error('blocked');
        },
      }),
    ).toBe('half');
  });

  it('steps through the sizes a tool supports and round again', () => {
    expect(nextSize('plans', 'panel')).toBe('half');
    expect(nextSize('plans', 'half')).toBe('workspace');
    expect(nextSize('plans', 'workspace')).toBe('panel');
    expect(nextSize('grid', 'workspace')).toBe('half');
    expect(nextSize('voice', 'panel')).toBe('panel');
  });

  it('starts plans and bulk editing beside the map or as the workspace', () => {
    expect(toolSpecs.plans.initial).not.toBe('panel');
    expect(toolSpecs.grid.initial).not.toBe('panel');
  });

  it('keeps the open tool and its size in the address', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const tools = TestBed.inject(Tools);
    const router = TestBed.inject(Router);
    localStorage.removeItem('cmdb.tool-size.plans');

    tools.toggle('plans');
    await new Promise((r) => setTimeout(r));
    expect(router.url).toContain('tool=plans');
    expect(router.url).toContain('size=half');
    expect(tools.size()).toBe('half');

    tools.setSize('workspace');
    await new Promise((r) => setTimeout(r));
    expect(router.url).toContain('size=workspace');
    expect(localStorage.getItem('cmdb.tool-size.plans')).toBe('workspace');

    await router.navigateByUrl('/?tool=plans&size=panel');
    expect(tools.size()).toBe('panel');

    tools.close();
    await new Promise((r) => setTimeout(r));
    expect(router.url).not.toContain('tool=');
    expect(tools.open()).toBeNull();
    localStorage.removeItem('cmdb.tool-size.plans');
  });
});
