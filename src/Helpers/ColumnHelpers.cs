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

namespace NodeGuard.Helpers;

public class ColumnDefault
{
    public string Name { get; }
    public bool Visibility { get;  }

    public ColumnDefault(string name, bool visibility = true)
    {
        Name = name;
        Visibility = visibility;
    }
}

public static class ColumnHelpers
{
    /// <summary>
    /// This method gets the fields from a class and returns a dictionary with the name of the field and the visibility of the column
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Dictionary<string, bool> GetColumnsDictionary<T>()
    {
        return typeof(T).GetFields().Select(p => (ColumnDefault)p.GetValue(null)).ToDictionary((c) => c.Name, (c) => c.Visibility);
    }
}
