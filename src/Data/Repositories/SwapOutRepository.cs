/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using AutoMapper;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories
{
   public class SwapOutRepository : ISwapOutRepository
   {
      private readonly IRepository<SwapOut> _repository;
      private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

      public SwapOutRepository(IRepository<SwapOut> repository, IDbContextFactory<ApplicationDbContext> dbContextFactory)
      {
         _repository = repository;
         _dbContextFactory = dbContextFactory;
      }

      public async Task<SwapOut?> GetById(int id)
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         var swap = await context.SwapOuts
            .Include(s => s.DestinationWallet)
            .Include(s => s.UserRequestor)
            .SingleOrDefaultAsync(s => s.Id == id);

         return swap;
      }

      public async Task<List<SwapOut>> GetByIds(List<int> ids)
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         var swaps = await context.SwapOuts
            .Include(s => s.DestinationWallet)
            .Include(s => s.UserRequestor)
            .Where(s => ids.Contains(s.Id))
            .ToListAsync();

         return swaps;
      }

      public async Task<List<SwapOut>> GetAll()
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         var swaps = await context.SwapOuts
            .Include(s => s.DestinationWallet)
            .ThenInclude(w => w!.Keys)
            .Include(s => s.UserRequestor)
            .Include(s => s.Node)
            .ToListAsync();

         return swaps;
      }

      public async Task<List<SwapOut>> GetAllPending()
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         var swaps = await context.SwapOuts
            .Include(s => s.DestinationWallet)
            .Include(s => s.UserRequestor)
            .Where(s => s.Status == SwapOutStatus.Pending)
            .ToListAsync();

         return swaps;
      }

      public async Task<(bool, string?)> AddAsync(SwapOut swap)
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         swap.SetCreationDatetime();
         swap.SetUpdateDatetime();

         return await _repository.AddAsync(swap, context);
      }

      public async Task<(bool, string?)> AddRangeAsync(List<SwapOut> swaps)
      {
         await using var context = await _dbContextFactory.CreateDbContextAsync();

         foreach (var swap in swaps)
         {
            swap.SetCreationDatetime();
            swap.SetUpdateDatetime();
         }

         return await _repository.AddRangeAsync(swaps, context);
      }

      public (bool, string?) Update(SwapOut swap)
      {
         using var context = _dbContextFactory.CreateDbContext();

         swap.SetUpdateDatetime();

         return _repository.Update(swap, context);
      }
   }
}