import { Component, computed, inject, input } from "@angular/core";
import { CpThemeService } from "@creator-pantry/ui";

@Component({
  imports: [],
  selector: "cp-logo",
  styleUrl: "./logo.css",
  templateUrl: "./logo.html",
})
export class CpLogoComponent {
  styles = input<string>("");

  readonly theme = inject(CpThemeService);

  readonly brandMarkSrc = computed(() =>
    this.theme.resolved() === "dark"
      ? "/images/logodark.png"
      : "/images/logo.png",
  );
}
