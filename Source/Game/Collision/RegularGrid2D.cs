﻿/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.GameMath;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Game.Collision
{
    public class RegularGrid2D<T, Node> where T : IModel where Node : BIHWrap<T>, new()
    {
        public const int CELL_NUMBER = 64;
        public const float HGRID_MAP_SIZE = (533.33333f * 64.0f);    // shouldn't be changed
        public const float CELL_SIZE = HGRID_MAP_SIZE / CELL_NUMBER;

        public RegularGrid2D()
        {
            for (int x = 0; x < CELL_NUMBER; ++x)
                nodes[x] = new Node[CELL_NUMBER];
        }

        public virtual void Insert(T value)
        {
            AxisAlignedBox bounds = value.GetBounds();
            Cell low = Cell.ComputeCell(bounds.Lo.X, bounds.Lo.Y);
            Cell high = Cell.ComputeCell(bounds.Hi.X, bounds.Hi.Y);
            for (int x = low.x; x <= high.x; ++x)
            {
                for (int y = low.y; y <= high.y; ++y)
                {
                    Node node = GetGrid(x, y);
                    node.Insert(value);
                    memberTable.Add(value, node);
                }
            }
        }

        public virtual void Remove(T value)
        {
            // Remove the member
            memberTable.Remove(value);
        }

        public virtual void Balance()
        {
            for (int x = 0; x < CELL_NUMBER; ++x)
            {
                for (int y = 0; y < CELL_NUMBER; ++y)
                {
                    Node n = nodes[x][y];
                    if (n != null)
                        n.Balance();
                }
            }
        }

        public bool Contains(T value) { return memberTable.ContainsKey(value); }
        public bool Empty() { return memberTable.Empty(); }

        public struct Cell
        {
            public int x, y;
            public static bool operator ==(Cell c1, Cell c2) { return c1.x == c2.x && c1.y == c2.y; }
            public static bool operator !=(Cell c1, Cell c2) { return !(c1 == c2); }

            public override bool Equals(object obj)
            {
                return base.Equals(obj);
            }

            public override int GetHashCode()
            {
                return x.GetHashCode() ^ y.GetHashCode();
            }

            public static Cell ComputeCell(float fx, float fy)
            {
                Cell c = new();
                c.x = (int)(fx * (1.0f / CELL_SIZE) + (CELL_NUMBER / 2f));
                c.y = (int)(fy * (1.0f / CELL_SIZE) + (CELL_NUMBER / 2f));
                return c;
            }

            public bool IsValid() { return x >= 0 && x < CELL_NUMBER && y >= 0 && y < CELL_NUMBER; }
        }

        Node GetGrid(int x, int y)
        {
            Cypher.Assert(x < CELL_NUMBER && y < CELL_NUMBER);
            if (nodes[x][y] == null)
                nodes[x][y] = new Node();
            return nodes[x][y];
        }

        public void IntersectRay(Ray ray, WorkerCallback intersectCallback, ref float max_dist)
        {
            IntersectRay(ray, intersectCallback, ref max_dist, ray.Origin + ray.Direction * max_dist);
        }

        public void IntersectRay(Ray ray, WorkerCallback intersectCallback, ref float max_dist, Vector3 end)
        {
            Cell cell = Cell.ComputeCell(ray.Origin.X, ray.Origin.Y);
            if (!cell.IsValid())
                return;

            Cell last_cell = Cell.ComputeCell(end.X, end.Y);

            if (cell == last_cell)
            {
                Node node = nodes[cell.x][cell.y];
                if (node != null)
                    node.IntersectRay(ray, intersectCallback, ref max_dist);
                return;
            }

            float voxel = CELL_SIZE;
            float kx_inv = ray.invDirection().X, bx = ray.Origin.X;
            float ky_inv = ray.invDirection().Y, by = ray.Origin.Y;

            int stepX, stepY;
            float tMaxX, tMaxY;
            if (kx_inv >= 0)
            {
                stepX = 1;
                float x_border = (cell.x + 1) * voxel;
                tMaxX = (x_border - bx) * kx_inv;
            }
            else
            {
                stepX = -1;
                float x_border = (cell.x - 1) * voxel;
                tMaxX = (x_border - bx) * kx_inv;
            }

            if (ky_inv >= 0)
            {
                stepY = 1;
                float y_border = (cell.y + 1) * voxel;
                tMaxY = (y_border - by) * ky_inv;
            }
            else
            {
                stepY = -1;
                float y_border = (cell.y - 1) * voxel;
                tMaxY = (y_border - by) * ky_inv;
            }

            float tDeltaX = voxel * Math.Abs(kx_inv);
            float tDeltaY = voxel * Math.Abs(ky_inv);
            do
            {
                Node node = nodes[cell.x][cell.y];
                if (node != null)
                {
                    node.IntersectRay(ray, intersectCallback, ref max_dist);
                }
                if (cell == last_cell)
                    break;
                if (tMaxX < tMaxY)
                {
                    tMaxX += tDeltaX;
                    cell.x += stepX;
                }
                else
                {
                    tMaxY += tDeltaY;
                    cell.y += stepY;
                }
            } while (cell.IsValid());
        }

        public void IntersectPoint(Vector3 point, WorkerCallback intersectCallback)
        {
            Cell cell = Cell.ComputeCell(point.X, point.Y);
            if (!cell.IsValid())
                return;

            Node node = nodes[cell.x][cell.y];
            if (node != null)
                node.IntersectPoint(point, intersectCallback);
        }

        // Optimized verson of intersectRay function for rays with vertical directions
        public void IntersectZAllignedRay(Ray ray, WorkerCallback intersectCallback, ref float max_dist)
        {
            Cell cell = Cell.ComputeCell(ray.Origin.X, ray.Origin.Y);
            if (!cell.IsValid())
                return;

            Node node = nodes[cell.x][cell.y];
            if (node != null)
                node.IntersectRay(ray, intersectCallback, ref max_dist);
        }

        MultiMap<T, Node> memberTable = new();
        Node[][] nodes = new Node[CELL_NUMBER][];
    }
}
