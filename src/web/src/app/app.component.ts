import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpBadgeComponent, CpButtonComponent, CpCardComponent, CpDialogComponent, CpFieldComponent, CpProgressComponent, CpQuickActionComponent, CpThemeService } from '@creator-pantry/ui';

interface Project { title:string; type:string; status:string; date:string; tone:'success'|'orange'|'purple'|'blue'; }
@Component({
 selector:'cp-root', standalone:true,
 imports:[FormsModule,CpBadgeComponent,CpButtonComponent,CpCardComponent,CpDialogComponent,CpFieldComponent,CpProgressComponent,CpQuickActionComponent],
 templateUrl:'./app.component.html', styleUrl:'./app.component.css', changeDetection:ChangeDetectionStrategy.OnPush
})
export class AppComponent {
 readonly theme=inject(CpThemeService);
 readonly menuOpen=signal(false); readonly dialog=signal<'create'|'ai'|null>(null); readonly query=signal('');
 readonly tasks=signal([{label:'Write blog post: Fall Soups',done:true},{label:'Generate social graphics',done:false},{label:'Edit recipe photos',done:false},{label:'Schedule next week',done:false},{label:'Review analytics',done:false}]);
 readonly projects:Project[]=[
  {title:'Cozy Fall Soup Blog Post',type:'Blog post',status:'Draft',date:'Sep 16',tone:'orange'},
  {title:'Roasted Vegetable Pasta',type:'Recipe',status:'Ready',date:'Sep 14',tone:'success'},
  {title:'Meal Prep Carousel',type:'Social set',status:'Scheduled',date:'Sep 13',tone:'blue'},
  {title:'Holiday Cookie Roundup',type:'Blog post',status:'In review',date:'Sep 12',tone:'purple'}];
 readonly filteredProjects=computed(()=>{const q=this.query().toLowerCase();return this.projects.filter(x=>`${x.title} ${x.type} ${x.status}`.toLowerCase().includes(q));});
 readonly completed=computed(()=>this.tasks().filter(x=>x.done).length); readonly progress=computed(()=>this.completed()/this.tasks().length*100);
 toggleTask(i:number){this.tasks.update(tasks=>tasks.map((x,n)=>n===i?{...x,done:!x.done}:x));}
 close(){this.dialog.set(null);} submit(){this.close();}
}
