import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Plain-language privacy and security notes, plus a note that feedback is welcome. Static content. */
@Component({
  selector: 'app-privacy',
  imports: [RouterLink],
  templateUrl: './privacy.html',
})
export class Privacy {}
