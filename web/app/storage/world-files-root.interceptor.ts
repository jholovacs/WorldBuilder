import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { WorldFilesStorageService } from './world-files-storage.service';

export const worldFilesRootInterceptor: HttpInterceptorFn = (req, next) => {
  const storage = inject(WorldFilesStorageService);
  const encoded = storage.encodedRootHeaderValue();
  if (!encoded) {
    return next(req);
  }
  return next(
    req.clone({
      setHeaders: {
        'X-World-Storage-Root': encoded,
      },
    }),
  );
};
