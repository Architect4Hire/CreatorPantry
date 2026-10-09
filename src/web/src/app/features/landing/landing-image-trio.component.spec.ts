import { TestBed } from '@angular/core/testing';

import { FoodImageService } from './food-image.service';
import { LandingImageTrioComponent } from './landing-image-trio.component';

describe('LandingImageTrioComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingImageTrioComponent],
      providers: [FoodImageService],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingImageTrioComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders three photos', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelectorAll('img').length).toBe(3);
  });

  it('shows three different photos', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const sources = Array.from(el.querySelectorAll('img')).map((img) => img.getAttribute('src'));

    expect(new Set(sources).size).toBe(3);
  });

  it('keeps the photos decorative: empty alt text and lazy loading', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    el.querySelectorAll('img').forEach((img) => {
      expect(img.getAttribute('alt')).toBe('');
      expect(img.getAttribute('loading')).toBe('lazy');
    });
  });

  it('flips the tilt when mirrored', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.trio')?.classList.contains('mirror')).toBe(false);

    fixture.componentRef.setInput('mirror', true);
    fixture.detectChanges();

    expect(el.querySelector('.trio')?.classList.contains('mirror')).toBe(true);
  });

  it('never repeats a photo between two trios on the same page', async () => {
    await TestBed.configureTestingModule({
      imports: [LandingImageTrioComponent],
      providers: [FoodImageService],
    }).compileComponents();
    const first = TestBed.createComponent(LandingImageTrioComponent);
    const second = TestBed.createComponent(LandingImageTrioComponent);
    first.detectChanges();
    second.detectChanges();

    const sources = [first, second].flatMap((fixture) =>
      Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('img')).map((img) => img.getAttribute('src')),
    );

    expect(new Set(sources).size).toBe(6);
  });
});
