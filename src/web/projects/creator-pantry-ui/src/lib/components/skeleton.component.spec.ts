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

  it('sweeps by background-position, never by moving the element', () => {
    const bone = mount('block').querySelector<HTMLElement>('.bone')!;
    document.body.appendChild(fixture.nativeElement);
    const style = getComputedStyle(bone);
    expect(style.animationName).toContain('cp-skeleton-sweep');
    expect(style.transform).toBe('none');
    fixture.nativeElement.remove();
  });
});
