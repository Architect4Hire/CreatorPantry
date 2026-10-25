import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CP_ILLUSTRATION_NAMES, CpIllustrationComponent, CpIllustrationName } from './illustration.component';

@Component({
  imports: [CpIllustrationComponent],
  template: `<span style="color: rgb(200, 20, 87)"><cp-illustration [name]="name()" /></span>`,
})
class HostComponent {
  readonly name = signal<CpIllustrationName>('recipe');
}

describe('CpIllustrationComponent', () => {
  for (const name of CP_ILLUSTRATION_NAMES) {
    it(`draws "${name}" as line art in the colour it sits in, hidden from assistive technology`, async () => {
      await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(HostComponent);
      fixture.componentInstance.name.set(name);
      fixture.detectChanges();
      document.body.appendChild(fixture.nativeElement);

      const host = (fixture.nativeElement as HTMLElement).querySelector('cp-illustration') as HTMLElement;
      const svg = host.querySelector('svg') as SVGSVGElement;
      expect(host.getAttribute('aria-hidden')).toBe('true');
      expect(svg.getAttribute('focusable')).toBe('false');
      // The wash and at least two strokes of its own: something is drawn, not an empty frame. And drawn as SVG —
      // an @switch inside <svg> that lost its namespace would make HTML elements of the same names, drawing nothing.
      const shapes = [...svg.querySelectorAll('path, rect, circle, ellipse')];
      expect(shapes.length).toBeGreaterThan(2);
      for (const shape of shapes) expect(shape.namespaceURI).toBe('http://www.w3.org/2000/svg');
      // currentColor, resolved: the drawing is the colour of what holds it, never a colour of its own.
      expect(getComputedStyle(svg).stroke).toBe('rgb(200, 20, 87)');
      expect(host.textContent?.trim()).toBe('');
      fixture.nativeElement.remove();
    });
  }

  it('draws each name differently', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    const drawings = new Set<string>();
    for (const name of CP_ILLUSTRATION_NAMES) {
      fixture.componentInstance.name.set(name);
      fixture.detectChanges();
      drawings.add((fixture.nativeElement as HTMLElement).querySelector('svg')!.innerHTML.replace(/<!--[\s\S]*?-->/g, ''));
    }
    expect(drawings.size).toBe(CP_ILLUSTRATION_NAMES.length);
  });
});
