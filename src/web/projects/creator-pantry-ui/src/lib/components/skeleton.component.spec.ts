import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpSkeletonComponent, CpSkeletonShape } from './skeleton.component';

@Component({
  imports: [CpSkeletonComponent],
  template: `<cp-skeleton [shape]="shape()" [count]="count()" [pictureAspect]="pictureAspect()" />`,
})
class HostComponent {
  readonly shape = signal<CpSkeletonShape>('line');
  readonly count = signal(1);
  readonly pictureAspect = signal<string | null>(null);
}

let fixture: ComponentFixture<HostComponent>;

function mount(shape: CpSkeletonShape, count = 1): HTMLElement {
  fixture = TestBed.createComponent(HostComponent);
  fixture.componentInstance.shape.set(shape);
  fixture.componentInstance.count.set(count);
  fixture.detectChanges();
  return (fixture.nativeElement as HTMLElement).querySelector('cp-skeleton')!;
}

describe('CpSkeletonComponent', () => {
  it('draws a block as a picture of the shape it is told, and 8rem tall when told none', () => {
    const host = mount('block');
    const block = host.querySelector<HTMLElement>('.bone.block')!;
    expect(block.getBoundingClientRect().height).toBeCloseTo(128, 0);

    fixture.componentInstance.pictureAspect.set('2 / 1');
    fixture.detectChanges();
    const box = block.getBoundingClientRect();
    expect(box.width / box.height).toBeCloseTo(2, 1);
  });

  it("draws a card's picture at 4:3 unless told the shape of the pictures coming", () => {
    const host = mount('card');
    const picture = host.querySelector<HTMLElement>('.bone.picture')!;
    const ratio = (): number => picture.getBoundingClientRect().width / picture.getBoundingClientRect().height;
    expect(ratio()).toBeCloseTo(4 / 3, 1);

    fixture.componentInstance.pictureAspect.set('1 / 1');
    fixture.detectChanges();
    expect(ratio()).toBeCloseTo(1, 1);
  });

  it('is hidden from assistive technology and holds nothing focusable', () => {
    const host = mount('card', 3);
    expect(host.getAttribute('aria-hidden')).toBe('true');
    expect(host.querySelector('[tabindex], a, button, input')).toBeNull();
    expect(host.textContent?.trim()).toBe('');
  });

  it('draws as many of the shape as it is asked for, and at least one', () => {
    expect(mount('line', 4).querySelectorAll('.bone.line').length).toBe(4);
    expect(mount('block', 2).querySelectorAll('.bone.block').length).toBe(2);
    expect(mount('circle', 3).querySelectorAll('.bone.circle').length).toBe(3);
    expect(mount('line', 0).querySelectorAll('.bone').length).toBe(1);
    expect(mount('line', Number.NaN).querySelectorAll('.bone').length).toBe(1);
    expect(mount('line', 500).querySelectorAll('.bone').length).toBe(24);
  });

  it('varies a block of lines and ends it short, so it reads as text rather than a table', () => {
    const widths = [...mount('line', 5).querySelectorAll<HTMLElement>('.bone.line')].map((line) => line.style.width);
    expect(new Set(widths).size).toBeGreaterThan(2);
    expect(widths.at(-1)).toBe('60%');
    expect(mount('line', 1).querySelector<HTMLElement>('.bone.line')!.style.width).toBe('100%');
  });

  it("draws a media card's silhouette: a picture, a title and two short lines", () => {
    const card = mount('card').querySelector('.card')!;
    expect(card.querySelector('.bone.picture')).toBeTruthy();
    expect(card.querySelectorAll('.bone.line').length).toBe(3);
    expect(card.querySelectorAll('.bone.line.title').length).toBe(1);
  });

  it('holds the space of what is coming and draws none of it', () => {
    const host = mount('card', 2);
    document.body.appendChild(fixture.nativeElement);
    expect(host.getBoundingClientRect().height).toBeGreaterThan(100);
    for (const shape of host.querySelectorAll<HTMLElement>('.card, .bone')) {
      expect(getComputedStyle(shape).visibility).toBe('hidden');
    }
    fixture.nativeElement.remove();
  });

  it('turns one spinner in the middle of that space, however many shapes it holds', () => {
    const host = mount('line', 4);
    document.body.appendChild(fixture.nativeElement);
    const spinners = host.querySelectorAll<HTMLElement>('cp-spinner');
    expect(spinners.length).toBe(1);

    const outer = host.getBoundingClientRect();
    const inner = spinners[0].getBoundingClientRect();
    expect(inner.left + inner.width / 2).toBeCloseTo(outer.left + outer.width / 2, 0);
    expect(inner.top + inner.height / 2).toBeCloseTo(outer.top + outer.height / 2, 0);
    expect(getComputedStyle(spinners[0]).visibility).toBe('visible');
    fixture.nativeElement.remove();
  });

  it('keeps the spinner near the top of a tall space, where it is seen rather than below the fold', () => {
    const host = mount('block', 6);
    document.body.appendChild(fixture.nativeElement);
    const outer = host.getBoundingClientRect();
    const inner = host.querySelector<HTMLElement>('cp-spinner')!.getBoundingClientRect();
    expect(outer.height).toBeGreaterThan(600);
    expect(inner.top - outer.top).toBeCloseTo(80, 0);
    fixture.nativeElement.remove();
  });

  it('draws a text-sized spinner for one line, and a larger one for anything more', () => {
    const size = (shape: CpSkeletonShape, count: number): number => {
      const spinner = mount(shape, count).querySelector<HTMLElement>('cp-spinner')!;
      document.body.appendChild(fixture.nativeElement);
      const height = spinner.getBoundingClientRect().height;
      fixture.nativeElement.remove();
      return height;
    };

    expect(size('line', 1)).toBeCloseTo(16, 0);
    expect(size('line', 4)).toBeCloseTo(28, 0);
    expect(size('block', 1)).toBeCloseTo(28, 0);
  });

  it('puts a one-line spinner where the words will begin, not in the middle of the line', () => {
    const host = mount('line', 1);
    document.body.appendChild(fixture.nativeElement);
    const outer = host.getBoundingClientRect();
    const inner = host.querySelector<HTMLElement>('cp-spinner')!.getBoundingClientRect();
    expect(inner.left).toBeCloseTo(outer.left, 0);
    fixture.nativeElement.remove();
  });
});
